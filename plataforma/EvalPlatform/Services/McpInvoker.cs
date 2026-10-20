using System.ClientModel;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using EvalPlatform.Domain;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using OpenAI;

namespace EvalPlatform.Services;

public sealed record McpToolInfo(string Name, string? Title, string? Description, JsonElement InputSchema);

// Modelo que usa las herramientas del MCP en los agentes "MCP con modelo".
public interface IAgentModelFactory
{
    IChatClient? Create(Agent agent);
}

public sealed class OpenAiCompatibleModelFactory(IJudgeProvider judge) : IAgentModelFactory
{
    public IChatClient? Create(Agent agent)
    {
        McpSettings mcp = agent.Mcp;
        if (string.IsNullOrWhiteSpace(mcp.ModelName))
        {
            // Sin modelo propio se usa el del juez IA.
            return judge.Get()?.ChatClient;
        }

        var options = new OpenAIClientOptions();
        if (!string.IsNullOrWhiteSpace(mcp.ModelEndpoint))
        {
            options.Endpoint = new Uri(mcp.ModelEndpoint.Trim());
        }
        string key = Templates.RenderText(mcp.ModelApiKey ?? "", new Dictionary<string, string>());
        return new OpenAIClient(new ApiKeyCredential(string.IsNullOrWhiteSpace(key) ? "sin-clave" : key), options)
            .GetChatClient(mcp.ModelName.Trim())
            .AsIChatClient();
    }
}

public sealed class McpConnectionException(string message, Exception? inner = null) : Exception(message, inner);

// Habla con servidores MCP por HTTP (Streamable HTTP o el transporte SSE anterior).
public sealed class McpInvoker(IHttpClientFactory httpFactory, IAgentModelFactory models)
{
    private static readonly JsonSerializerOptions s_json = new(McpJsonUtilities.DefaultOptions)
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public async Task<IReadOnlyList<McpToolInfo>> ListToolsAsync(Agent agent, CancellationToken ct)
    {
        using CancellationTokenSource timeout = Timeout(agent, ct);
        await using McpClient client = await ConnectAsync(agent, new Dictionary<string, string>(), timeout.Token);
        IList<McpClientTool> tools = await client.ListToolsAsync(cancellationToken: timeout.Token);
        return tools.Select(t => new McpToolInfo(t.Name, t.Title, t.Description, t.JsonSchema)).ToList();
    }

    public async Task<AgentInvocation> InvokeAsync(Agent agent, string input, IReadOnlyDictionary<string, string> row, CancellationToken ct, string? traceId = null)
    {
        Dictionary<string, string> vars = AgentInvoker.Variables(input, row);
        using CancellationTokenSource timeout = Timeout(agent, ct);
        try
        {
            await using McpClient client = await ConnectAsync(agent, vars, timeout.Token, traceId);
            return agent.Kind == AgentKinds.McpModel
                ? await RunWithModelAsync(agent, client, input, vars, timeout.Token)
                : await CallToolAsync(agent, client, vars, timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new AgentInvocation { Error = $"El servidor MCP no respondió en {agent.TimeoutSeconds} segundos." };
        }
        catch (McpConnectionException ex)
        {
            return new AgentInvocation { Error = ex.Message };
        }
    }

    // Llamada directa: tools/call con la herramienta y los argumentos del caso.
    private static async Task<AgentInvocation> CallToolAsync(Agent agent, McpClient client, Dictionary<string, string> vars, CancellationToken ct)
    {
        string tool = Templates.RenderText(agent.Mcp.ToolTemplate, vars).Trim();
        if (tool.Length == 0)
        {
            return new AgentInvocation { Error = "No se indicó la herramienta. Revisa el campo Herramienta del agente o la columna del caso." };
        }

        string argumentsText = Templates.RenderBody(agent.Mcp.ArgumentsTemplate, vars).Trim();
        Dictionary<string, object?> arguments;
        try
        {
            arguments = argumentsText.Length == 0
                ? []
                : JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(argumentsText)!.ToDictionary(kv => kv.Key, kv => (object?)kv.Value);
        }
        catch (JsonException ex)
        {
            return new AgentInvocation { RequestBody = argumentsText, Error = $"Los argumentos no son un objeto JSON válido: {ex.Message}" };
        }

        var request = new JsonObject { ["herramienta"] = tool, ["argumentos"] = JsonNode.Parse(argumentsText.Length == 0 ? "{}" : argumentsText) };
        var call = new ToolCallRecord { Name = tool, Arguments = request["argumentos"]!.ToJsonString(s_json) };

        var stopwatch = Stopwatch.StartNew();
        JsonObject json;
        string output;
        try
        {
            CallToolResult result = await client.CallToolAsync(tool, arguments, cancellationToken: ct);
            stopwatch.Stop();
            json = JsonSerializer.SerializeToNode(result, s_json)!.AsObject();
            // isError siempre presente, para poder compararlo con "Es igual a".
            json["isError"] = result.IsError == true;
            output = ResultText(result);
        }
        catch (McpException ex)
        {
            // Error de protocolo (herramienta inexistente, argumentos inválidos…): es una respuesta
            // del servidor, así que se evalúa como cualquier otra.
            stopwatch.Stop();
            json = new JsonObject { ["isError"] = true, ["error"] = ex.Message };
            output = ex.Message;
        }
        call.Result = output;

        return new AgentInvocation
        {
            RequestBody = request.ToJsonString(s_json),
            HttpStatus = 200,
            RawResponse = json.ToJsonString(s_json),
            Json = json,
            Output = output,
            Tools = [tool],
            ToolCalls = [call],
            LatencyMs = stopwatch.ElapsedMilliseconds,
        };
    }

    // Con modelo: el modelo recibe la entrada y las herramientas del MCP, y decide cuáles llamar.
    private async Task<AgentInvocation> RunWithModelAsync(Agent agent, McpClient client, string input, Dictionary<string, string> vars, CancellationToken ct)
    {
        IChatClient? model = models.Create(agent);
        if (model is null)
        {
            return new AgentInvocation { Error = "No hay modelo: configura uno en el agente o el juez IA del servidor." };
        }

        IList<McpClientTool> tools = await client.ListToolsAsync(cancellationToken: ct);
        HashSet<string>? allowed = string.IsNullOrWhiteSpace(agent.Mcp.AllowedTools)
            ? null
            : agent.Mcp.AllowedTools.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
        List<AITool> offered = tools.Where(t => allowed is null || allowed.Contains(t.Name)).Select(t => (AITool)new TextResultTool(t)).ToList();

        var messages = new List<ChatMessage>();
        if (!string.IsNullOrWhiteSpace(agent.Mcp.SystemPrompt))
        {
            messages.Add(new ChatMessage(ChatRole.System, Templates.RenderText(agent.Mcp.SystemPrompt, vars)));
        }
        messages.Add(new ChatMessage(ChatRole.User, input));

        using IChatClient chat = model.AsBuilder().UseFunctionInvocation(configure: f => f.MaximumIterationsPerRequest = 10).Build();
        var stopwatch = Stopwatch.StartNew();
        ChatResponse response;
        try
        {
            response = await chat.GetResponseAsync(messages, new ChatOptions { Tools = offered, Temperature = agent.Mcp.Temperature }, ct);
        }
        catch (Exception ex) when (ex is ClientResultException or HttpRequestException or InvalidOperationException)
        {
            return new AgentInvocation { Error = $"El modelo falló: {ex.Message}" };
        }
        stopwatch.Stop();

        var results = response.Messages.SelectMany(m => m.Contents).OfType<FunctionResultContent>().ToDictionary(r => r.CallId, r => ResultText(r.Result));
        List<ToolCallRecord> calls = response.Messages
            .SelectMany(m => m.Contents)
            .OfType<FunctionCallContent>()
            .Select(c => new ToolCallRecord
            {
                Name = c.Name,
                Arguments = JsonSerializer.Serialize(c.Arguments ?? new Dictionary<string, object?>(), s_json),
                Result = results.GetValueOrDefault(c.CallId),
            })
            .ToList();

        int? tokens = response.Usage?.TotalTokenCount is long total ? (int)total : null;
        var json = new JsonObject
        {
            ["output"] = response.Text,
            ["toolCalls"] = JsonSerializer.SerializeToNode(calls, s_json),
            ["herramientasOfrecidas"] = new JsonArray(offered.Select(t => (JsonNode)JsonValue.Create(t.Name)!).ToArray()),
            ["tokens"] = tokens,
        };

        return new AgentInvocation
        {
            RequestBody = JsonSerializer.Serialize(new { sistema = agent.Mcp.SystemPrompt, entrada = input, herramientas = offered.Select(t => t.Name) }, s_json),
            HttpStatus = 200,
            RawResponse = json.ToJsonString(s_json),
            Json = json,
            Output = response.Text,
            Tools = calls.Select(c => c.Name).ToList(),
            ToolCalls = calls,
            // Lo que devolvieron las herramientas es el contexto con que respondió el modelo.
            Context = calls.Select(c => c.Result).OfType<string>().Where(r => r.Length > 0).ToList(),
            Tokens = tokens,
            LatencyMs = stopwatch.ElapsedMilliseconds,
        };
    }

    private async Task<McpClient> ConnectAsync(Agent agent, IReadOnlyDictionary<string, string> vars, CancellationToken ct, string? traceId = null)
    {
        Uri endpoint;
        try
        {
            endpoint = new Uri(Templates.RenderText(agent.Url, vars, Uri.EscapeDataString));
        }
        catch (UriFormatException)
        {
            throw new McpConnectionException($"La URL del servidor MCP no es válida: {agent.Url}");
        }

        var transport = new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Endpoint = endpoint,
                TransportMode = HttpTransportMode.AutoDetect,
                Name = agent.Name,
                AdditionalHeaders = WithTraceParent(agent.Headers.ToDictionary(h => h.Key, h => Templates.RenderText(h.Value, vars)), traceId),
            },
            httpFactory.CreateClient(AgentInvoker.HttpClientName),
            NullLoggerFactory.Instance,
            ownsHttpClient: true);
        try
        {
            return await McpClient.CreateAsync(
                transport,
                new McpClientOptions { ClientInfo = new Implementation { Name = "plataforma-evaluaciones", Version = "1.0" } },
                NullLoggerFactory.Instance,
                ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or McpException or InvalidOperationException or IOException or JsonException)
        {
            throw new McpConnectionException($"No se pudo conectar con el servidor MCP: {ex.Message}", ex);
        }
    }

    private static Dictionary<string, string> WithTraceParent(Dictionary<string, string> headers, string? traceId)
    {
        if (traceId is not null)
        {
            headers["traceparent"] = TraceStore.TraceParent(traceId);
        }
        return headers;
    }

    private static CancellationTokenSource Timeout(Agent agent, CancellationToken ct)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(agent.TimeoutSeconds, 1, 1800)));
        return cts;
    }

    private static string ResultText(CallToolResult result)
    {
        string text = string.Join("\n", result.Content.OfType<TextContentBlock>().Select(t => t.Text));
        if (text.Length == 0 && result.StructuredContent is JsonElement structured)
        {
            text = structured.GetRawText();
        }
        return text;
    }

    // Lo que devuelve FunctionInvokingChatClient al invocar una McpClientTool: según el caso, un
    // CallToolResult, un bloque de contenido o una lista de bloques, ya serializados a JSON.
    private static string ResultText(object? result) => result switch
    {
        null => "",
        string s => s,
        CallToolResult r => ResultText(r),
        ContentBlock b => b is TextContentBlock t ? t.Text : JsonSerializer.Serialize(b, s_json),
        JsonElement e => ElementText(e),
        _ => ElementText(JsonSerializer.SerializeToElement(result, s_json)),
    };

    private static string ElementText(JsonElement e)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.String:
                return e.GetString() ?? "";
            case JsonValueKind.Array:
                return string.Join("\n", e.EnumerateArray().Select(ElementText).Where(t => t.Length > 0));
            case JsonValueKind.Object:
                if (e.TryGetProperty("content", out JsonElement content) && content.ValueKind == JsonValueKind.Array)
                {
                    string text = ElementText(content);
                    return text.Length > 0 || !e.TryGetProperty("structuredContent", out JsonElement structured) ? text : structured.GetRawText();
                }
                if (e.TryGetProperty("text", out JsonElement inner) && inner.ValueKind == JsonValueKind.String)
                {
                    return inner.GetString() ?? "";
                }
                return e.GetRawText();
            default:
                return e.GetRawText();
        }
    }
}

// Le entrega al modelo el resultado de la herramienta como texto (no el JSON del protocolo),
// igual que los clientes MCP habituales. Los errores de la herramienta llegan como "Error: …".
internal sealed class TextResultTool(McpClientTool tool) : DelegatingAIFunction(tool)
{
    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        CallToolResult result = await tool.CallAsync(arguments, cancellationToken: cancellationToken);
        string text = string.Join("\n", result.Content.OfType<TextContentBlock>().Select(t => t.Text));
        if (text.Length == 0 && result.StructuredContent is JsonElement structured)
        {
            text = structured.GetRawText();
        }
        return result.IsError == true ? $"Error: {text}" : text;
    }
}

// Elige cómo llamar al agente según su tipo. Con traceId, le pasa al agente el encabezado
// traceparent para que sus trazas OpenTelemetry lleguen asociadas al caso.
public sealed class AgentDispatcher(AgentInvoker http, McpInvoker mcp)
{
    public async Task<AgentInvocation> InvokeAsync(Agent agent, string input, IReadOnlyDictionary<string, string> row, CancellationToken ct, string? traceId = null)
    {
        AgentInvocation result = agent.Kind switch
        {
            AgentKinds.Code => new AgentInvocation { Error = "Este agente lo ejecuta tu propio código: la plataforma no puede llamarlo. Ejecuta una evaluación y deja corriendo tu ejecutor." },
            AgentKinds.McpDirect or AgentKinds.McpModel => await mcp.InvokeAsync(agent, input, row, ct, traceId),
            _ => await http.InvokeAsync(agent, input, row, ct, traceId),
        };
        result.TraceId = traceId;
        return result;
    }
}

// Completa una respuesta con lo que dicen las trazas del agente (tokens, llamadas al modelo).
public static class TraceEnricher
{
    public static async Task ApplyAsync(TraceStore traces, Agent agent, AgentInvocation invocation, CancellationToken ct)
    {
        if (!agent.TracesEnabled || invocation.TraceId is null || !invocation.Succeeded)
        {
            return;
        }
        TraceSummary? summary = await traces.WaitAsync(invocation.TraceId, TimeSpan.FromSeconds(Math.Clamp(agent.TraceWaitSeconds, 1, 45)), ct);
        if (summary is null)
        {
            return;
        }
        invocation.Trace = summary;
        // Si la respuesta ya traía tokens, mandan esos; si no, los de las trazas.
        invocation.Tokens ??= summary.TotalTokens;
    }
}
