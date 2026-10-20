using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using ClosedXML.Excel;
using EvalPlatform.Domain;
using EvalPlatform.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace EvalPlatform.Tests;

public sealed class VariantTemplateTests
{
    [Fact]
    public void Json_inserta_numeros_y_vacio_queda_null()
    {
        var vars = new Dictionary<string, string> { ["input"] = "123", ["modelo"] = "gpt-5-mini", ["temp"] = "0.2", ["filtros"] = """{"pais":"co"}""", ["texto"] = "no es json" };
        JsonNode body = JsonNode.Parse(Templates.RenderBody(
            """{ "model": "{{modelo}}", "temperature": "{{json:temp}}", "top_k": "{{json:no_existe}}", "f": "{{json:filtros}}", "t": "{{json:texto}}", "q": "{{input}}", "mezcla": "t={{json:temp}}" }""",
            vars))!;
        Assert.Equal("gpt-5-mini", body["model"]!.GetValue<string>());
        Assert.Equal(0.2, body["temperature"]!.GetValue<double>());
        Assert.Null(body["top_k"]);
        Assert.Equal("co", body["f"]!["pais"]!.GetValue<string>());
        Assert.Equal("no es json", body["t"]!.GetValue<string>());
        // {{input}} sigue siendo texto aunque parezca un número.
        Assert.Equal("123", body["q"]!.GetValue<string>());
        Assert.Equal("t=0.2", body["mezcla"]!.GetValue<string>());
    }

    [Fact]
    public void Placeholders_sin_env_y_parametros_ganan_a_columnas()
    {
        Assert.Equal(["modelo", "temp"], Templates.Placeholders("{{modelo}} {{env:CLAVE}} {{json:temp}}"));
        var merged = Variant.Merge(new Dictionary<string, string> { ["modelo"] = "de-la-columna", ["id"] = "7" }, new Dictionary<string, string> { ["MODELO"] = "de-la-variante" });
        Assert.Equal("de-la-variante", merged["modelo"]);
        Assert.Equal("7", merged["id"]);
    }
}

// Variantes: los mismos casos con varias configuraciones, lado a lado.
public sealed class VariantFlowTests : IDisposable
{
    private const string Token = "token-variantes";
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"eval-var-{Guid.NewGuid():N}.db");
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    public VariantFlowTests()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:Eval", $"Data Source={_dbPath}");
            builder.UseSetting("Demo:CrearEjemplo", "false");
            builder.UseSetting("Ejecutores:Token", Token);
            builder.ConfigureServices(services =>
            {
                services.AddHttpClient(AgentInvoker.HttpClientName)
                    .ConfigurePrimaryHttpMessageHandler(() => new DelegatingToServer(() => _factory!.Server.CreateHandler()));
                services.RemoveAll<IJudgeProvider>();
                services.AddSingleton<IJudgeProvider>(new FakeJudgeProvider(null));
            });
        });
        _client = _factory.CreateClient();
    }

    private const string Tickets =
        "ticket,debe_responder\n" +
        "\"Buenos días, no puedo entrar al sistema, me sale que mi usuario está bloqueado.\",si\n" +
        "¿Cómo creo un usuario nuevo que solo pueda consultar y no modificar nada?,si\n" +
        "La sincronización con el banco está fallando con error 504,no\n";

    private static readonly object[] s_checks =
    [
        new { type = "igual", label = "Decisión", expectedColumn = "debe_responder", responsePath = "output.puede_responder", blocking = true },
    ];

    [Fact]
    public async Task Compara_dos_bases_de_conocimiento_caso_por_caso()
    {
        int agentId = await CreateAsync("/api/agentes", new
        {
            name = "Helpdesk",
            url = "http://localhost/demo/helpdesk",
            bodyTemplate = """{ "mensaje": "{{input}}", "base": "{{base}}" }""",
            responsePath = "output.respuesta",
            timeoutSeconds = 30,
        });
        int datasetId = await UploadAsync("Tickets", Tickets);
        var variants = new object[]
        {
            new { name = "Base actual", parameters = new Dictionary<string, string> { ["base"] = "actual" } },
            new { name = "Base ampliada", parameters = new Dictionary<string, string> { ["base"] = "ampliada" } },
        };

        // Errores que dejarían la comparación igual sin que se note.
        await AssertBadAsync(Evaluation(agentId, datasetId, new { name = "A", parameters = new Dictionary<string, string> { ["modelo"] = "x" } }), "no usa el parámetro «modelo»");
        await AssertBadAsync(Evaluation(agentId, datasetId, new { name = "A", parameters = new Dictionary<string, string> { ["base"] = "x" } }, new { name = "a", parameters = new Dictionary<string, string> { ["base"] = "y" } }), "dos variantes llamadas");
        await AssertBadAsync(Evaluation(agentId, datasetId, new { name = "A", parameters = new Dictionary<string, string> { ["base"] = "x" } }, new { name = "B", parameters = new Dictionary<string, string> { ["base"] = "x" } }), "son iguales");
        await AssertBadAsync(Evaluation(agentId, datasetId, new { name = "A", parameters = new Dictionary<string, string> { ["input"] = "x" } }), "no sirve como nombre");

        int single = await CreateAsync("/api/evaluaciones", Evaluation(agentId, datasetId, variants[0]));
        HttpResponseMessage tooFew = await _client.PostAsJsonAsync($"/api/evaluaciones/{single}/comparar", new { });
        Assert.Equal(HttpStatusCode.BadRequest, tooFew.StatusCode);

        int evaluationId = await CreateAsync("/api/evaluaciones", Evaluation(agentId, datasetId, variants));
        JsonNode started = (await PostJson($"/api/evaluaciones/{evaluationId}/comparar", new { note = "¿Sirve agregar el manual de usuarios?" }))!;
        int comparisonId = started["comparisonId"]!.GetValue<int>();
        Assert.Equal(2, started["runIds"]!.AsArray().Count);

        JsonNode data = await WaitForComparisonAsync(comparisonId);
        JsonNode report = data["report"]!;
        Assert.Equal("¿Sirve agregar el manual de usuarios?", data["comparison"]!["note"]!.GetValue<string>());

        JsonArray summaries = report["variants"]!.AsArray();
        Assert.True(summaries.All(v => v!["status"]!.GetValue<string>() == "Completed" && v["completed"]!.GetValue<int>() == 3), data.ToJsonString());
        Assert.Equal(["Base actual", "Base ampliada"], summaries.Select(v => v!["name"]!.GetValue<string>()));
        Assert.Equal(2, summaries[0]!["passed"]!.GetValue<int>());
        Assert.Equal(3, summaries[1]!["passed"]!.GetValue<int>());
        Assert.Equal("ampliada", summaries[1]!["parameters"]!["base"]!.GetValue<string>());
        Assert.Equal(summaries[1]!["runId"]!.GetValue<int>(), report["bestRunId"]!.GetValue<int>());
        Assert.Contains("«Base ampliada»", report["verdict"]!.GetValue<string>());
        Assert.Equal(1.0, summaries[1]!["checkPassRates"]![0]!.GetValue<double>());

        // Solo cambia el caso de crear usuarios: la base actual no tiene ese manual.
        JsonArray cases = report["cases"]!.AsArray();
        Assert.Equal(3, cases.Count);
        Assert.Equal([1], cases.Where(c => c!["differs"]!.GetValue<bool>()).Select(c => c!["rowIndex"]!.GetValue<int>()));
        JsonArray cells = cases[1]!["cells"]!.AsArray();
        Assert.Equal("Failed", cells[0]!["outcome"]!.GetValue<string>());
        Assert.Equal("Passed", cells[1]!["outcome"]!.GetValue<string>());
        Assert.Contains("Administración > Usuarios", cells[1]!["output"]!.GetValue<string>());

        // Cada corrida sabe su variante; la evaluación lista la comparativa.
        int bestRun = summaries[1]!["runId"]!.GetValue<int>();
        JsonNode run = (await _client.GetFromJsonAsync<JsonNode>($"/api/corridas/{bestRun}"))!;
        Assert.Equal("Base ampliada", run["run"]!["variant"]!.GetValue<string>());
        Assert.Equal(comparisonId, run["run"]!["comparisonId"]!.GetValue<int>());
        JsonNode evaluation = (await _client.GetFromJsonAsync<JsonNode>($"/api/evaluaciones/{evaluationId}"))!;
        Assert.Equal(comparisonId, evaluation["comparisons"]![0]!["id"]!.GetValue<int>());
        Assert.Equal(2, evaluation["comparisons"]![0]!["runs"]!.AsArray().Count);

        // Una variante sola: su corrida anterior es la de la misma variante.
        int again = (await PostJson($"/api/evaluaciones/{evaluationId}/ejecutar", new { variant = "base ampliada" }))!["runId"]!.GetValue<int>();
        JsonNode againRun = await WaitForRunAsync(again);
        Assert.Equal("Base ampliada", againRun["run"]!["variant"]!.GetValue<string>());
        Assert.Equal(bestRun, againRun["summary"]!["previousRunId"]!.GetValue<int>());
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.PostAsJsonAsync($"/api/evaluaciones/{evaluationId}/ejecutar", new { variant = "no existe" })).StatusCode);

        // Comparar corridas sueltas de la misma evaluación (antes y después).
        int firstRun = summaries[0]!["runId"]!.GetValue<int>();
        JsonNode adhoc = (await _client.GetFromJsonAsync<JsonNode>($"/api/comparativas/corridas?ids={firstRun},{again}"))!;
        Assert.Equal(2, adhoc["report"]!["variants"]!.AsArray().Count);
        int otherRun = (await PostJson($"/api/evaluaciones/{single}/ejecutar", new { }))!["runId"]!.GetValue<int>();
        await WaitForRunAsync(otherRun);
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.GetAsync($"/api/comparativas/corridas?ids={firstRun},{otherRun}")).StatusCode);

        // Excel con una columna por variante.
        HttpResponseMessage excel = await _client.GetAsync($"/api/comparativas/{comparisonId}/excel");
        Assert.Equal(HttpStatusCode.OK, excel.StatusCode);
        using (var workbook = new XLWorkbook(await excel.Content.ReadAsStreamAsync()))
        {
            Assert.Contains("«Base ampliada»", workbook.Worksheet("Resumen").Cell(5, 2).GetString());
            IXLWorksheet casos = workbook.Worksheet("Casos");
            Assert.Equal("Base actual · resultado", casos.Cell(1, 4).GetString());
            Assert.Equal("Sí", casos.Cell(3, 3).GetString());
        }

        // Borrar la comparativa borra sus corridas.
        Assert.Equal(HttpStatusCode.NoContent, (await _client.DeleteAsync($"/api/comparativas/{comparisonId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync($"/api/corridas/{bestRun}")).StatusCode);
    }

    [Fact]
    public async Task Otro_agente_como_variante_y_parametros_para_el_ejecutor()
    {
        int platformAgent = await CreateAsync("/api/agentes", new
        {
            name = "Helpdesk",
            url = "http://localhost/demo/helpdesk",
            bodyTemplate = """{ "mensaje": "{{input}}" }""",
            responsePath = "output.respuesta",
            timeoutSeconds = 30,
        });
        int codeAgent = await CreateAsync("/api/agentes", new { name = "Servicio Java", kind = "codigo", executor = "red-soporte" });
        int datasetId = await UploadAsync("Tickets", Tickets);

        // Al código propio le llega cualquier parámetro: no se valida contra plantillas.
        int evaluationId = await CreateAsync("/api/evaluaciones", Evaluation(platformAgent, datasetId,
            new { name = "Plataforma", parameters = new Dictionary<string, string>() },
            new { name = "Java + modelo grande", agentId = codeAgent, parameters = new Dictionary<string, string> { ["modelo"] = "grande", ["top_k"] = "10" } }));
        int comparisonId = (await PostJson($"/api/evaluaciones/{evaluationId}/comparar", new { }))!["comparisonId"]!.GetValue<int>();

        var executor = _factory.CreateClient();
        executor.DefaultRequestHeaders.Add("X-Ejecutor-Token", Token);
        JsonArray tasks = new();
        for (int i = 0; i < 50 && tasks.Count == 0; i++)
        {
            HttpResponseMessage claim = await executor.PostAsJsonAsync("/api/ejecutores/tomar", new { group = "red-soporte", worker = "w", mode = "codigo", max = 5, waitSeconds = 0 });
            tasks = (await claim.Content.ReadFromJsonAsync<JsonArray>())!;
            if (tasks.Count == 0)
            {
                await Task.Delay(100);
            }
        }
        Assert.Equal(3, tasks.Count);
        Assert.Equal("Java + modelo grande", tasks[0]!["variant"]!.GetValue<string>());
        Assert.Equal("grande", tasks[0]!["parameters"]!["modelo"]!.GetValue<string>());
        Assert.Equal("10", tasks[0]!["parameters"]!["top_k"]!.GetValue<string>());

        foreach (JsonNode? task in tasks)
        {
            await executor.PostAsJsonAsync("/api/ejecutores/resultado", new { taskId = task!["taskId"]!.GetValue<long>(), output = "Lo escalo.", tokens = 50 });
        }
        JsonNode report = (await WaitForComparisonAsync(comparisonId))["report"]!;
        Assert.Equal(["Helpdesk", "Servicio Java"], report["variants"]!.AsArray().Select(v => v!["agentName"]!.GetValue<string>()));
        Assert.Equal(50, report["variants"]![1]!["averageTokens"]!.GetValue<double>());

        // Un agente usado en una variante no se puede borrar.
        Assert.Equal(HttpStatusCode.Conflict, (await _client.DeleteAsync($"/api/agentes/{codeAgent}")).StatusCode);
    }

    private static object Evaluation(int agentId, int datasetId, params object[] variants) =>
        new { name = "Variantes", agentId, datasetId, inputColumn = "ticket", checks = s_checks, variants };

    private async Task AssertBadAsync(object evaluation, string message)
    {
        HttpResponseMessage response = await _client.PostAsJsonAsync("/api/evaluaciones", evaluation);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(message, (await response.Content.ReadFromJsonAsync<JsonNode>())!["error"]!.GetValue<string>());
    }

    private async Task<int> UploadAsync(string name, string csv)
    {
        using var form = new MultipartFormDataContent
        {
            { new StringContent(name), "nombre" },
            { new ByteArrayContent(Encoding.UTF8.GetBytes(csv)), "archivo", "casos.csv" },
        };
        HttpResponseMessage upload = await _client.PostAsync("/api/datasets/archivo", form);
        Assert.Equal(HttpStatusCode.Created, upload.StatusCode);
        return (await upload.Content.ReadFromJsonAsync<JsonNode>())!["id"]!.GetValue<int>();
    }

    private async Task<int> CreateAsync(string url, object body) => (await PostJson(url, body))!["id"]!.GetValue<int>();

    private async Task<JsonNode?> PostJson(string url, object body)
    {
        HttpResponseMessage response = await _client.PostAsJsonAsync(url, body);
        string text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{url}: {(int)response.StatusCode} {text}");
        return JsonNode.Parse(text);
    }

    private async Task<JsonNode> WaitForComparisonAsync(int id)
    {
        for (int i = 0; i < 150; i++)
        {
            JsonNode data = (await _client.GetFromJsonAsync<JsonNode>($"/api/comparativas/{id}"))!;
            if (data["report"]!["finished"]!.GetValue<bool>())
            {
                return data;
            }
            await Task.Delay(100);
        }
        throw new TimeoutException($"La comparativa {id} no terminó.");
    }

    private async Task<JsonNode> WaitForRunAsync(int runId)
    {
        for (int i = 0; i < 100; i++)
        {
            JsonNode run = (await _client.GetFromJsonAsync<JsonNode>($"/api/corridas/{runId}"))!;
            if (run["run"]!["status"]!.GetValue<string>() is not ("Queued" or "Running"))
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

    // El servidor de pruebas solo existe después de construir la app. Los casos corren en paralelo,
    // así que el primer uso se protege: InnerHandler no se puede cambiar después del primer envío.
    private sealed class DelegatingToServer(Func<HttpMessageHandler> server) : DelegatingHandler
    {
        private readonly object _lock = new();
        private volatile bool _initialized;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (!_initialized)
            {
                lock (_lock)
                {
                    if (!_initialized)
                    {
                        InnerHandler = server();
                        _initialized = true;
                    }
                }
            }
            return base.SendAsync(request, cancellationToken);
        }
    }
}
