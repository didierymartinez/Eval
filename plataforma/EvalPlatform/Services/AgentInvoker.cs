using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using EvalPlatform.Domain;

namespace EvalPlatform.Services;

public sealed class AgentInvocation
{
    public string RequestBody { get; init; } = "";
    public int? HttpStatus { get; init; }
    public string? RawResponse { get; init; }
    public JsonNode? Json { get; init; }
    public string Output { get; init; } = "";
    public List<string> Context { get; init; } = [];
    public List<string> Tools { get; init; } = [];
    public long LatencyMs { get; init; }
    public string? Error { get; init; }
    public bool Succeeded => Error is null;
}

// Llama al agente con los datos de un caso y saca respuesta, contexto y herramientas.
public sealed class AgentInvoker(IHttpClientFactory httpFactory)
{
    public const string HttpClientName = "agentes";
    private const int MaxRawLength = 200_000;

    public static Dictionary<string, string> Variables(string input, IReadOnlyDictionary<string, string> row)
    {
        var vars = new Dictionary<string, string>(row, StringComparer.OrdinalIgnoreCase)
        {
            ["input"] = input,
        };
        return vars;
    }

    public async Task<AgentInvocation> InvokeAsync(Agent agent, string input, IReadOnlyDictionary<string, string> row, CancellationToken cancellationToken)
    {
        Dictionary<string, string> vars = Variables(input, row);
        HttpMethod method = HttpMethod.Parse(string.IsNullOrWhiteSpace(agent.Method) ? "POST" : agent.Method.Trim().ToUpperInvariant());
        string body = method == HttpMethod.Get ? "" : Templates.RenderBody(agent.BodyTemplate, vars);

        Uri url;
        try
        {
            url = new Uri(Templates.RenderText(agent.Url, vars, Uri.EscapeDataString));
        }
        catch (UriFormatException)
        {
            return new AgentInvocation { RequestBody = body, Error = $"La URL del agente no es válida: {agent.Url}" };
        }

        using var request = new HttpRequestMessage(method, url);
        string contentType = "application/json";
        foreach (var (name, value) in agent.Headers)
        {
            string rendered = Templates.RenderText(value, vars);
            if (name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
            {
                contentType = rendered;
            }
            else
            {
                request.Headers.TryAddWithoutValidation(name, rendered);
            }
        }
        if (body.Length > 0)
        {
            request.Content = new StringContent(body, Encoding.UTF8);
            request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(agent.TimeoutSeconds, 1, 1800)));

        var stopwatch = Stopwatch.StartNew();
        HttpResponseMessage response;
        string raw;
        try
        {
            response = await httpFactory.CreateClient(HttpClientName).SendAsync(request, timeout.Token);
            raw = await response.Content.ReadAsStringAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new AgentInvocation { RequestBody = body, LatencyMs = stopwatch.ElapsedMilliseconds, Error = $"El agente no respondió en {agent.TimeoutSeconds} segundos." };
        }
        catch (HttpRequestException ex)
        {
            return new AgentInvocation { RequestBody = body, LatencyMs = stopwatch.ElapsedMilliseconds, Error = $"No se pudo conectar con el agente: {ex.Message}" };
        }
        stopwatch.Stop();

        using (response)
        {
            string truncated = raw.Length > MaxRawLength ? raw[..MaxRawLength] : raw;
            if (!response.IsSuccessStatusCode)
            {
                return new AgentInvocation
                {
                    RequestBody = body,
                    HttpStatus = (int)response.StatusCode,
                    RawResponse = truncated,
                    LatencyMs = stopwatch.ElapsedMilliseconds,
                    Error = $"El agente respondió HTTP {(int)response.StatusCode} {response.ReasonPhrase}.",
                };
            }

            JsonNode? json = null;
            try
            {
                json = raw.Length > 0 ? JsonNode.Parse(raw) : null;
            }
            catch (JsonException)
            {
            }

            // Respuesta que no es JSON: se toma el texto completo como respuesta.
            string output = json is null ? raw : JsonPath.SelectText(json, agent.ResponsePath);
            return new AgentInvocation
            {
                RequestBody = body,
                HttpStatus = (int)response.StatusCode,
                RawResponse = truncated,
                Json = json,
                Output = output,
                Context = json is null || string.IsNullOrWhiteSpace(agent.ContextPath) ? [] : JsonPath.SelectTexts(json, agent.ContextPath),
                Tools = json is null || string.IsNullOrWhiteSpace(agent.ToolsPath) ? [] : [.. JsonPath.Select(json, agent.ToolsPath).Select(JsonPath.ToText).Where(t => t.Length > 0)],
                LatencyMs = stopwatch.ElapsedMilliseconds,
            };
        }
    }
}
