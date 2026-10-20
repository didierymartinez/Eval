using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using EvalPlatform.Domain;
using EvalPlatform.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace EvalPlatform.Tests;

public class ExpectedCallTests
{
    private static readonly List<ToolCallRecord> s_calls =
    [
        new() { Name = "buscar_cliente", Arguments = """{"nombre":"Constructora Andina"}""" },
        new() { Name = "consultar_cartera", Arguments = """{"cliente":123,"incluir_vencidas":true,"filtro":{"anio":2026,"tipo":"Factura"}}""" },
    ];

    [Theory]
    [InlineData("""consultar_cartera {"cliente": "123"}""", true)]           // "123" == 123
    [InlineData("""consultar_cartera: {"filtro": {"tipo": "factura"}}""", true)] // sin mayúsculas, objeto parcial
    [InlineData("""{"nombre": "constructora andina"}""", true)]             // sin nombre: cualquier herramienta
    [InlineData("buscar_cliente", true)]                                     // solo el nombre
    [InlineData("""consultar_cartera {"cliente": "456"}""", false)]
    [InlineData("""crear_ticket {"prioridad": "alta"}""", false)]
    [InlineData("""consultar_cartera {"no_existe": 1}""", false)]
    public void Coincide_con_subconjunto_de_argumentos(string expected, bool match)
    {
        ExpectedCall call = ExpectedCall.ParseAll(expected).Single();
        Assert.Equal(match, call.FindMatch(s_calls) is not null);
    }

    [Fact]
    public void Varias_lineas_y_errores_de_formato()
    {
        Assert.Equal(2, ExpectedCall.ParseAll("buscar_cliente\nconsultar_cartera {\"cliente\": 123}\n").Count);
        Assert.Throws<FormatException>(() => ExpectedCall.ParseAll("consultar_cartera {cliente: 123"));
        string why = ExpectedCall.ParseAll("""crear_ticket {"prioridad": "alta"}""").Single().Describe(s_calls);
        Assert.Contains("buscar_cliente", why);
    }

    [Fact]
    public async Task Comparacion_de_argumentos_en_agentes_http_requiere_ruta()
    {
        var runner = new CheckRunner(new FakeJudgeProvider(null));
        var noPath = new CaseData(new Agent { Name = "a", Url = "http://x", ToolsPath = "t" }, "q", new Dictionary<string, string>(), new AgentInvocation { ToolCalls = s_calls });
        Assert.Equal(CheckStatus.Error, (await runner.RunAsync(new() { Type = CheckTypes.ToolArguments, ExpectedValue = "buscar_cliente" }, noPath, CancellationToken.None)).Status);

        var mcp = noPath with { Agent = new Agent { Name = "a", Url = "http://x", Kind = AgentKinds.McpModel } };
        CheckResult ok = await runner.RunAsync(new() { Type = CheckTypes.ToolArguments, ExpectedValue = "consultar_cartera {\"cliente\": \"123\"}" }, mcp, CancellationToken.None);
        Assert.Equal(CheckStatus.Passed, ok.Status);
    }
}

// El servidor MCP de demostración corre dentro de la misma app; el "modelo" es un guion.
public sealed class McpFlowTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"eval-mcp-{Guid.NewGuid():N}.db");
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    public McpFlowTests()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:Eval", $"Data Source={_dbPath}");
            builder.UseSetting("Demo:CrearEjemplo", "false");
            builder.ConfigureServices(services =>
            {
                services.AddHttpClient(AgentInvoker.HttpClientName)
                    .ConfigurePrimaryHttpMessageHandler(() => new LazyServerHandler(() => _factory!.Server.CreateHandler()));
                services.RemoveAll<IAgentModelFactory>();
                services.AddSingleton<IAgentModelFactory>(new ScriptedModelFactory());
                services.RemoveAll<IJudgeProvider>();
                services.AddSingleton<IJudgeProvider>(new FakeJudgeProvider(null));
            });
        });
        _client = _factory.CreateClient();
    }

    private const string McpUrl = "http://localhost/demo/mcp";

    [Fact]
    public async Task Lista_herramientas_del_servidor()
    {
        JsonArray tools = (await (await _client.PostAsJsonAsync("/api/agentes/herramientas", new { name = "m", url = McpUrl, kind = "mcp", mcp = new { toolTemplate = "x" } })).Content.ReadFromJsonAsync<JsonArray>())!;
        Assert.Equal(["buscar_cliente", "consultar_cartera", "crear_ticket"], tools.Select(t => t!["name"]!.GetValue<string>()).Order());
        JsonNode cartera = tools.First(t => t!["name"]!.GetValue<string>() == "consultar_cartera")!;
        Assert.Contains("saldo", cartera["description"]!.GetValue<string>());
        Assert.Equal("string", cartera["inputSchema"]!["properties"]!["cliente"]!["type"]!.GetValue<string>());
    }

    [Fact]
    public async Task Servidor_inexistente_da_error_claro()
    {
        HttpResponseMessage r = await _client.PostAsJsonAsync("/api/agentes/herramientas", new { name = "m", url = "http://localhost/no-hay-mcp", kind = "mcp", mcp = new { toolTemplate = "x" }, timeoutSeconds = 10 });
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Contains("No se pudo conectar con el servidor MCP", (await r.Content.ReadFromJsonAsync<JsonNode>())!["error"]!.GetValue<string>());
    }

    [Fact]
    public async Task Llamada_directa_evalua_resultado_y_errores()
    {
        int agentId = await CreateAsync("/api/agentes", new { name = "MCP", url = McpUrl, kind = "mcp", mcp = new { toolTemplate = "{{herramienta}}", argumentsTemplate = "{{argumentos}}" }, timeoutSeconds = 30 });
        int datasetId = await UploadCsvAsync(
            "caso,herramienta,argumentos,debe_contener,es_error\n" +
            "ok,consultar_cartera,\"{\"\"cliente\"\": \"\"123\"\"}\",1.200.000,no\n" +
            "no existe,consultar_cartera,\"{\"\"cliente\"\": \"\"999\"\"}\",no existe,si\n" +
            "minusculas,buscar_cliente,\"{\"\"nombre\"\": \"\"constructora andina\"\"}\",123,no\n" +
            "herramienta inexistente,borrar_todo,{},,si\n" +
            "json roto,consultar_cartera,{cliente: 1},,no\n");
        int evaluationId = await CreateAsync("/api/evaluaciones", new
        {
            name = "MCP directo",
            agentId,
            datasetId,
            inputColumn = "caso",
            checks = new object[]
            {
                new { type = "contiene", expectedColumn = "debe_contener", blocking = true },
                new { type = "igual", expectedColumn = "es_error", responsePath = "isError", blocking = true },
                new { type = "herramientas", expectedColumn = "herramienta", blocking = true },
            },
        });

        JsonArray results = await RunAsync(evaluationId);
        Assert.Equal(["Passed", "Passed", "Failed", "Passed", "Error"], results.Select(r => r!["outcome"]!.GetValue<string>()));
        Assert.Contains("El cliente 123 (Constructora Andina) debe $1.200.000", results[0]!["output"]!.GetValue<string>());
        Assert.Equal("consultar_cartera", results[0]!["toolCalls"]![0]!["name"]!.GetValue<string>());
        Assert.Equal("""{"cliente":"123"}""", results[0]!["toolCalls"]![0]!["arguments"]!.GetValue<string>());
        Assert.Contains("No se encontró", results[2]!["output"]!.GetValue<string>());
        Assert.Contains("borrar_todo", results[3]!["output"]!.GetValue<string>());
        Assert.Contains("no son un objeto JSON válido", results[4]!["error"]!.GetValue<string>());
    }

    [Fact]
    public async Task Modelo_con_mcp_registra_herramientas_argumentos_y_resultados()
    {
        int agentId = await CreateAsync("/api/agentes", new { name = "Modelo+MCP", url = McpUrl, kind = "mcp_modelo", mcp = new { systemPrompt = "Eres el asistente.", allowedTools = "buscar_cliente, consultar_cartera" }, timeoutSeconds = 30 });

        JsonNode test = (await (await _client.PostAsJsonAsync("/api/agentes/probar", new { agent = new { name = "Modelo+MCP", url = McpUrl, kind = "mcp_modelo", mcp = new { allowedTools = "buscar_cliente, consultar_cartera" } }, input = "¿Cuánto debe Constructora Andina?" })).Content.ReadFromJsonAsync<JsonNode>())!;
        Assert.True(test["ok"]!.GetValue<bool>(), test.ToJsonString());
        // crear_ticket no se ofreció.
        Assert.Equal(["buscar_cliente", "consultar_cartera"], JsonNode.Parse(test["raw"]!.GetValue<string>())!["herramientasOfrecidas"]!.AsArray().Select(n => n!.GetValue<string>()).Order());

        int datasetId = await UploadCsvAsync(
            "input,herramientas_esperadas,argumentos_esperados\n" +
            "¿Cuánto debe Constructora Andina?,buscar_cliente|consultar_cartera,\"consultar_cartera {\"\"cliente\"\": \"\"123\"\"}\"\n" +
            "¿Cuánto debe Inversiones del Valle?,buscar_cliente|consultar_cartera,\"consultar_cartera {\"\"cliente\"\": \"\"456\"\"}\"\n" +
            "Hola,ninguna,\n");
        int evaluationId = await CreateAsync("/api/evaluaciones", new
        {
            name = "Modelo + MCP",
            agentId,
            datasetId,
            inputColumn = "input",
            checks = new object[]
            {
                new { type = "herramientas", expectedColumn = "herramientas_esperadas", blocking = true },
                new { type = "herramientas_orden", expectedColumn = "herramientas_esperadas", blocking = false },
                new { type = "argumentos", expectedColumn = "argumentos_esperados", blocking = true },
            },
        });

        JsonArray results = await RunAsync(evaluationId);
        Assert.Equal(["Passed", "Failed", "Passed"], results.Select(r => r!["outcome"]!.GetValue<string>()));

        JsonNode first = results[0]!;
        Assert.Equal(["buscar_cliente", "consultar_cartera"], first["toolCalls"]!.AsArray().Select(t => t!["name"]!.GetValue<string>()));
        Assert.Contains("código 123", first["toolCalls"]![0]!["result"]!.GetValue<string>());
        Assert.Contains("1.200.000", first["output"]!.GetValue<string>());
        // Lo que devolvieron las herramientas queda como contexto (para "Respaldada por los documentos").
        Assert.Equal(2, first["context"]!.AsArray().Count);
        // Tres vueltas del modelo (buscar, consultar, responder) de 21 tokens cada una.
        Assert.Equal(63, first["tokens"]!.GetValue<int>());

        // El guion se salta buscar_cliente para "Inversiones del Valle" y manda el nombre como código.
        JsonArray secondChecks = results[1]!["checks"]!.AsArray();
        Assert.Equal("Failed", secondChecks[0]!["status"]!.GetValue<string>());
        Assert.Contains("Faltó llamar: buscar_cliente", secondChecks[0]!["reason"]!.GetValue<string>());
        Assert.Equal("Failed", secondChecks[2]!["status"]!.GetValue<string>());
        Assert.Contains("Inversiones del Valle", secondChecks[2]!["reason"]!.GetValue<string>());

        Assert.Equal("Skipped", results[2]!["checks"]![2]!["status"]!.GetValue<string>());
    }

    private async Task<int> CreateAsync(string url, object body)
    {
        HttpResponseMessage r = await _client.PostAsJsonAsync(url, body);
        string text = await r.Content.ReadAsStringAsync();
        Assert.True(r.IsSuccessStatusCode, text);
        return JsonNode.Parse(text)!["id"]!.GetValue<int>();
    }

    private async Task<int> UploadCsvAsync(string csv)
    {
        using var form = new MultipartFormDataContent { { new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes(csv)), "archivo", "casos.csv" } };
        HttpResponseMessage r = await _client.PostAsync("/api/datasets/archivo", form);
        Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
        return (await r.Content.ReadFromJsonAsync<JsonNode>())!["id"]!.GetValue<int>();
    }

    private async Task<JsonArray> RunAsync(int evaluationId)
    {
        int runId = (await (await _client.PostAsJsonAsync($"/api/evaluaciones/{evaluationId}/ejecutar", new { })).Content.ReadFromJsonAsync<JsonNode>())!["runId"]!.GetValue<int>();
        for (int i = 0; i < 150; i++)
        {
            JsonNode run = (await _client.GetFromJsonAsync<JsonNode>($"/api/corridas/{runId}"))!;
            if (run["run"]!["status"]!.GetValue<string>() is not ("Queued" or "Running"))
            {
                return (await _client.GetFromJsonAsync<JsonArray>($"/api/corridas/{runId}/resultados"))!;
            }
            await Task.Delay(100);
        }
        throw new TimeoutException();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
        SqliteConnection.ClearAllPools();
        File.Delete(_dbPath);
    }

    private sealed class LazyServerHandler(Func<HttpMessageHandler> server) : DelegatingHandler
    {
        private readonly object _lock = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (_lock)
            {
                InnerHandler ??= server();
            }
            return base.SendAsync(request, cancellationToken);
        }
    }

    private sealed class ScriptedModelFactory : IAgentModelFactory
    {
        public IChatClient? Create(Agent agent) => new ScriptedModel();
    }

    // "Modelo" que decide las llamadas según la pregunta y responde con el último resultado.
    private sealed class ScriptedModel : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var list = messages.ToList();
            string question = list.Last(m => m.Role == ChatRole.User).Text;
            List<string> results = list.SelectMany(m => m.Contents).OfType<FunctionResultContent>().Select(r => r.Result?.ToString() ?? "").ToList();
            Assert.NotNull(options?.Tools);

            ChatMessage reply = (question, results.Count) switch
            {
                (var q, 0) when q.Contains("Constructora Andina") => Call("buscar_cliente", new() { ["nombre"] = "Constructora Andina" }),
                (var q, 1) when q.Contains("Constructora Andina") => Call("consultar_cartera", new() { ["cliente"] = "123" }),
                (var q, 0) when q.Contains("Inversiones del Valle") => Call("consultar_cartera", new() { ["cliente"] = "Inversiones del Valle" }),
                (_, 0) => new ChatMessage(ChatRole.Assistant, "Puedo consultar cartera y buscar clientes."),
                _ => new ChatMessage(ChatRole.Assistant, $"Según el sistema: {results[^1]}"),
            };
            return Task.FromResult(new ChatResponse(reply) { Usage = new UsageDetails { TotalTokenCount = 21 } });
        }

        private static ChatMessage Call(string name, Dictionary<string, object?> args) =>
            new(ChatRole.Assistant, [new FunctionCallContent(Guid.NewGuid().ToString("N"), name, args)]);

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
