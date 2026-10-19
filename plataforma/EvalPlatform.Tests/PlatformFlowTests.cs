using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using EvalPlatform.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace EvalPlatform.Tests;

// Recorre la plataforma como lo haría la interfaz: agente → casos → evaluación → corrida.
// El agente es el endpoint de demostración de la misma app y el juez es de mentira.
public sealed class PlatformFlowTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"eval-test-{Guid.NewGuid():N}.db");
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    public PlatformFlowTests()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:Eval", $"Data Source={_dbPath}");
            builder.UseSetting("Demo:CrearEjemplo", "false");
            builder.ConfigureServices(services =>
            {
                // Las llamadas a "agentes" van al servidor de pruebas en memoria.
                services.AddHttpClient(AgentInvoker.HttpClientName)
                    .ConfigurePrimaryHttpMessageHandler(() => new DelegatingToServer(() => _factory!.Server.CreateHandler()));
                // Juez: 5 solo cuando califica contra la referencia del caso 1, 1 en los demás.
                services.RemoveAll<IJudgeProvider>();
                services.AddSingleton<IJudgeProvider>(new FakeJudgeProvider(new FakeJudgeClient(p => p.Contains("Se bloquea por 30 minutos") ? 5 : 1)));
            });
        });
        _client = _factory.CreateClient();
    }

    [Fact]
    public async Task Flujo_completo_con_csv_juez_y_excel()
    {
        // 1. Agente (y la prueba de conexión)
        var agent = new
        {
            name = "Helpdesk",
            url = "http://localhost/demo/helpdesk",
            method = "POST",
            headers = new Dictionary<string, string>(),
            bodyTemplate = """{ "mensaje": "{{input}}" }""",
            responsePath = "output.respuesta",
            contextPath = "intermediateSteps[*].observation",
            toolsPath = "intermediateSteps[*].action.tool",
            timeoutSeconds = 30,
        };
        JsonNode test = (await PostJson("/api/agentes/probar", new { agent, input = "Mi usuario está bloqueado" }))!;
        Assert.True(test["ok"]!.GetValue<bool>(), test.ToJsonString());
        Assert.Contains("30 minutos", test["output"]!.GetValue<string>());
        Assert.Single(test["context"]!.AsArray());

        int agentId = (await PostJson("/api/agentes", agent))!["id"]!.GetValue<int>();

        // 2. Casos desde un CSV con punto y coma, como lo guarda Excel en español
        string csv = "ticket;debe_responder;referencia\n" +
            "No puedo entrar, usuario bloqueado;si;Se bloquea por 30 minutos.\n" +
            "Error 504 al sincronizar el banco;no;\n" +
            "¿Cómo creo un usuario de solo consulta?;si;Administración > Usuarios > rol Consulta.\n";
        using var form = new MultipartFormDataContent
        {
            { new StringContent("Tickets"), "nombre" },
            { new ByteArrayContent(Encoding.UTF8.GetBytes(csv)), "archivo", "tickets.csv" },
        };
        HttpResponseMessage upload = await _client.PostAsync("/api/datasets/archivo", form);
        Assert.Equal(HttpStatusCode.Created, upload.StatusCode);
        int datasetId = (await upload.Content.ReadFromJsonAsync<JsonNode>())!["id"]!.GetValue<int>();

        // 3. Evaluación: una mal configurada se rechaza con un mensaje claro
        HttpResponseMessage bad = await _client.PostAsJsonAsync("/api/evaluaciones", new { name = "x", agentId, datasetId, inputColumn = "no_existe", checks = new[] { new { type = "contiene", expectedValue = "a" } } });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Contains("columna", (await bad.Content.ReadFromJsonAsync<JsonNode>())!["error"]!.GetValue<string>());

        int evaluationId = (await PostJson("/api/evaluaciones", new
        {
            name = "N1",
            agentId,
            datasetId,
            inputColumn = "ticket",
            checks = new object[]
            {
                new { type = "igual", expectedColumn = "debe_responder", responsePath = "output.puede_responder", blocking = true },
                new { type = "correccion", expectedColumn = "referencia", blocking = true },
                new { type = "cobertura", expectedColumn = "referencia", blocking = false },
                new { type = "herramientas", expectedValue = "base_conocimiento", blocking = true },
            },
        }))!["id"]!.GetValue<int>();

        // 4. Corrida
        int runId = (await PostJson($"/api/evaluaciones/{evaluationId}/ejecutar", new { }))!["runId"]!.GetValue<int>();
        JsonNode run = await WaitForRunAsync(runId);
        Assert.Equal("Completed", run["run"]!["status"]!.GetValue<string>());
        Assert.Equal(3, run["run"]!["completed"]!.GetValue<int>());

        JsonArray results = (await _client.GetFromJsonAsync<JsonArray>($"/api/corridas/{runId}/resultados"))!;
        string[] outcomes = results.Select(r => r!["outcome"]!.GetValue<string>()).ToArray();
        Assert.Equal(["Passed", "Passed", "Failed"], outcomes);

        // Caso 1: decisión, juez y cobertura pasan.
        Assert.All(results[0]!["checks"]!.AsArray(), c => Assert.Equal("Passed", c!["status"]!.GetValue<string>()));
        // Caso 2 (N2): sin referencia, el juez se omite; escalar era lo correcto.
        Assert.Equal("Skipped", results[1]!["checks"]![1]!["status"]!.GetValue<string>());
        // Caso 3: escaló un caso de N1 y los documentos no traían la respuesta.
        JsonArray third = results[2]!["checks"]!.AsArray();
        Assert.Equal("Failed", third[0]!["status"]!.GetValue<string>());
        Assert.Equal("Failed", third[2]!["status"]!.GetValue<string>());

        JsonNode summary = run["summary"]!;
        Assert.Equal(2, summary["passed"]!.GetValue<int>());
        Assert.Equal(4, summary["checks"]!.AsArray().Count);

        // Segunda corrida: compara con la anterior.
        int secondRun = (await PostJson($"/api/evaluaciones/{evaluationId}/ejecutar", new { }))!["runId"]!.GetValue<int>();
        JsonNode second = await WaitForRunAsync(secondRun);
        Assert.Equal(runId, second["summary"]!["previousRunId"]!.GetValue<int>());
        Assert.Empty(second["summary"]!["changes"]!.AsArray());

        // 5. Excel
        HttpResponseMessage excel = await _client.GetAsync($"/api/corridas/{runId}/excel");
        Assert.Equal(HttpStatusCode.OK, excel.StatusCode);
        using var workbook = new ClosedXML.Excel.XLWorkbook(await excel.Content.ReadAsStreamAsync());
        Assert.Equal(["Resumen", "Casos"], workbook.Worksheets.Select(w => w.Name));
        Assert.Equal("Fallido", workbook.Worksheet("Casos").Cell(4, 3).GetString());

        // No se puede borrar un agente en uso.
        Assert.Equal(HttpStatusCode.Conflict, (await _client.DeleteAsync($"/api/agentes/{agentId}")).StatusCode);
    }

    [Fact]
    public async Task Agente_caido_queda_como_error_y_no_como_fallo()
    {
        int agentId = (await PostJson("/api/agentes", new { name = "Caído", url = "http://localhost/no-existe", bodyTemplate = """{ "q": "{{input}}" }""", responsePath = "", timeoutSeconds = 5 }))!["id"]!.GetValue<int>();
        using var form = new MultipartFormDataContent { { new ByteArrayContent(Encoding.UTF8.GetBytes("input\nhola\n")), "archivo", "c.csv" } };
        int datasetId = (await (await _client.PostAsync("/api/datasets/archivo", form)).Content.ReadFromJsonAsync<JsonNode>())!["id"]!.GetValue<int>();
        int evaluationId = (await PostJson("/api/evaluaciones", new { name = "E", agentId, datasetId, inputColumn = "input", checks = new[] { new { type = "contiene", expectedValue = "hola", blocking = true } } }))!["id"]!.GetValue<int>();
        int runId = (await PostJson($"/api/evaluaciones/{evaluationId}/ejecutar", new { }))!["runId"]!.GetValue<int>();

        JsonNode run = await WaitForRunAsync(runId);
        Assert.Equal(1, run["run"]!["errors"]!.GetValue<int>());
        JsonArray results = (await _client.GetFromJsonAsync<JsonArray>($"/api/corridas/{runId}/resultados"))!;
        Assert.Contains("HTTP 404", results[0]!["error"]!.GetValue<string>());
    }

    private async Task<JsonNode?> PostJson(string url, object body)
    {
        HttpResponseMessage response = await _client.PostAsJsonAsync(url, body);
        string text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{url}: {(int)response.StatusCode} {text}");
        return JsonNode.Parse(text);
    }

    private async Task<JsonNode> WaitForRunAsync(int runId)
    {
        for (int i = 0; i < 100; i++)
        {
            JsonNode run = (await _client.GetFromJsonAsync<JsonNode>($"/api/corridas/{runId}"))!;
            string status = run["run"]!["status"]!.GetValue<string>();
            if (status is not ("Queued" or "Running"))
            {
                return run;
            }
            await Task.Delay(100);
        }
        throw new TimeoutException($"La corrida {runId} no terminó.");
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.Delete(_dbPath);
    }

    // El servidor de pruebas solo existe después de construir la app; se resuelve al primer uso.
    private sealed class DelegatingToServer(Func<HttpMessageHandler> server) : DelegatingHandler
    {
        private bool _initialized;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (!_initialized)
            {
                InnerHandler = server();
                _initialized = true;
            }
            return base.SendAsync(request, cancellationToken);
        }
    }
}
