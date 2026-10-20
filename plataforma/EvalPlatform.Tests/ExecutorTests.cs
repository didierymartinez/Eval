using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using EvalPlatform.Data;
using EvalPlatform.Domain;
using EvalPlatform.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace EvalPlatform.Tests;

// Corridas que ejecutan los ejecutores remotos (otra red u otra tecnología) y no la plataforma.
public sealed class ExecutorTests : IDisposable
{
    private const string Token = "token-de-prueba";
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"eval-exec-{Guid.NewGuid():N}.db");
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;
    private readonly HttpClient _executor;

    public ExecutorTests()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:Eval", $"Data Source={_dbPath}");
            builder.UseSetting("Demo:CrearEjemplo", "false");
            builder.UseSetting("Ejecutores:Token", Token);
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IJudgeProvider>();
                services.AddSingleton<IJudgeProvider>(new FakeJudgeProvider(null));
            });
        });
        _client = _factory.CreateClient();
        _executor = _factory.CreateClient();
        _executor.DefaultRequestHeaders.Add("X-Ejecutor-Token", Token);
        _executor.DefaultRequestHeaders.Add("X-Ejecutor-Nombre", "prueba");
    }

    [Fact]
    public async Task Sin_token_o_con_token_malo_se_rechaza()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.PostAsJsonAsync("/api/ejecutores/tomar", new { group = "g" })).StatusCode);
        var bad = _factory.CreateClient();
        bad.DefaultRequestHeaders.Add("Authorization", "Bearer otro");
        Assert.Equal(HttpStatusCode.Unauthorized, (await bad.PostAsJsonAsync("/api/ejecutores/tomar", new { group = "g" })).StatusCode);
        var ok = _factory.CreateClient();
        ok.DefaultRequestHeaders.Add("Authorization", $"Bearer {Token}");
        Assert.Equal(HttpStatusCode.OK, (await ok.PostAsJsonAsync("/api/ejecutores/tomar", new { group = "g" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await ok.PostAsJsonAsync("/api/ejecutores/tomar", new { group = "Con Espacios" })).StatusCode);
    }

    [Fact]
    public async Task Agente_de_codigo_propio_completo()
    {
        // Validación: "código propio" exige grupo.
        HttpResponseMessage invalid = await _client.PostAsJsonAsync("/api/agentes", new { name = "x", kind = "codigo" });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);

        int agentId = await CreateAsync("/api/agentes", new { name = "Servicio Java", kind = "codigo", executor = "Red-Finanzas" });
        int runId = await StartRunAsync(agentId, "input,herramientas_esperadas,debe_contener\n¿Cuánto debe el 123?,consultar_cartera,1.200.000\nHola,ninguna,cartera\n");

        // La plataforma no lo ejecuta: queda esperando.
        await Task.Delay(300);
        JsonNode run = await RunAsync(runId);
        Assert.Equal("Running", run["run"]!["status"]!.GetValue<string>());
        Assert.Equal("red-finanzas", run["run"]!["executor"]!.GetValue<string>());

        // El ejecutor genérico no toma casos de "código propio"; otro grupo tampoco.
        Assert.Empty(await ClaimAsync("red-finanzas", "generico", 5));
        Assert.Empty(await ClaimAsync("otra-red", "codigo", 5));

        JsonArray tasks = await ClaimAsync("red-finanzas", "codigo", 5);
        Assert.Equal(2, tasks.Count);
        Assert.Equal("¿Cuánto debe el 123?", tasks[0]!["input"]!.GetValue<string>());
        Assert.Equal("consultar_cartera", tasks[0]!["row"]!["herramientas_esperadas"]!.GetValue<string>());
        // Ya prestados: no se reparten dos veces.
        Assert.Empty(await ClaimAsync("red-finanzas", "codigo", 5));

        JsonNode first = await ReportAsync(new
        {
            taskId = tasks[0]!["taskId"]!.GetValue<long>(),
            output = "El cliente 123 debe $1.200.000.",
            toolCalls = new[] { new { name = "consultar_cartera", arguments = (object)"{\"cliente\":\"123\"}" } },
            latencyMs = 950,
        });
        Assert.Equal("Passed", first["resultado"]!.GetValue<string>());

        JsonNode second = await ReportAsync(new { taskId = tasks[1]!["taskId"]!.GetValue<long>(), error = "TimeoutException: el servicio no respondió" });
        Assert.Equal("Error", second["resultado"]!.GetValue<string>());

        // Duplicado: se rechaza.
        HttpResponseMessage again = await _executor.PostAsJsonAsync("/api/ejecutores/resultado", new { taskId = tasks[0]!["taskId"]!.GetValue<long>(), output = "x" });
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);

        run = await RunAsync(runId);
        Assert.Equal("Completed", run["run"]!["status"]!.GetValue<string>());
        Assert.Equal(1, run["run"]!["passed"]!.GetValue<int>());
        Assert.Equal(1, run["run"]!["errors"]!.GetValue<int>());

        JsonArray results = (await _client.GetFromJsonAsync<JsonArray>($"/api/corridas/{runId}/resultados"))!;
        Assert.Equal("""{"cliente":"123"}""", results[0]!["toolCalls"]![0]!["arguments"]!.GetValue<string>());
        Assert.Equal(950, results[0]!["latencyMs"]!.GetValue<long>());

        JsonNode status = (await _client.GetFromJsonAsync<JsonNode>("/api/ejecutores/estado"))!;
        JsonNode group = status["grupos"]!.AsArray().Single(g => g!["grupo"]!.GetValue<string>() == "red-finanzas")!;
        Assert.Equal("prueba", group["ejecutores"]![0]!["nombre"]!.GetValue<string>());
        Assert.True(group["ejecutores"]![0]!["conectado"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Agente_http_via_ejecutor_generico_recibe_la_configuracion()
    {
        int agentId = await CreateAsync("/api/agentes", new
        {
            name = "Interno",
            kind = "http",
            executor = "red-interna",
            url = "http://servicio-interno.local/preguntar",
            headers = new Dictionary<string, string> { ["X-Clave"] = "{{env:CLAVE_INTERNA}}" },
            bodyTemplate = """{ "q": "{{input}}" }""",
            responsePath = "respuesta",
        });
        int runId = await StartRunAsync(agentId, "input,debe_contener\nhola,hola\n");

        JsonArray tasks = await ClaimAsync("red-interna", "generico", 1, waitSeconds: 5);
        JsonNode agent = Assert.Single(tasks)!["agent"]!;
        Assert.Equal("http://servicio-interno.local/preguntar", agent["url"]!.GetValue<string>());
        // El secreto viaja como referencia; se resuelve en el ejecutor.
        Assert.Equal("{{env:CLAVE_INTERNA}}", agent["headers"]!["X-Clave"]!.GetValue<string>());

        await ReportAsync(new { taskId = tasks[0]!["taskId"]!.GetValue<long>(), output = "hola, ¿en qué te ayudo?", raw = """{"respuesta":"hola, ¿en qué te ayudo?"}""" });
        Assert.Equal("Completed", (await RunAsync(runId))["run"]!["status"]!.GetValue<string>());
    }

    [Fact]
    public async Task Caso_prestado_vencido_vuelve_a_repartirse_y_cancelar_corta()
    {
        int agentId = await CreateAsync("/api/agentes", new { name = "C", kind = "codigo", executor = "g1" });
        int runId = await StartRunAsync(agentId, "input\nuno\ndos\n");

        JsonArray firstClaim = await ClaimAsync("g1", "codigo", 1);
        long taskId = firstClaim[0]!["taskId"]!.GetValue<long>();

        // El ejecutor se cayó: su préstamo vence.
        await using (var db = new EvalDbContext(new DbContextOptionsBuilder<EvalDbContext>().UseSqlite($"Data Source={_dbPath}").Options))
        {
            await db.RunTasks.Where(t => t.Id == taskId).ExecuteUpdateAsync(s => s.SetProperty(t => t.LeaseUntil, DateTimeOffset.UtcNow.AddMinutes(-1)));
        }
        JsonArray secondClaim = await ClaimAsync("g1", "codigo", 5);
        Assert.Equal(2, secondClaim.Count);
        Assert.Contains(secondClaim, t => t!["taskId"]!.GetValue<long>() == taskId);

        Assert.Equal(HttpStatusCode.Accepted, (await _client.PostAsync($"/api/corridas/{runId}/cancelar", null)).StatusCode);
        Assert.Equal("Cancelled", (await RunAsync(runId))["run"]!["status"]!.GetValue<string>());
        HttpResponseMessage late = await _executor.PostAsJsonAsync("/api/ejecutores/resultado", new { taskId, output = "tarde" });
        Assert.Equal(HttpStatusCode.Conflict, late.StatusCode);
        Assert.Empty(await ClaimAsync("g1", "codigo", 5));
    }

    [Fact]
    public async Task Probar_un_agente_de_codigo_explica_que_no_se_puede_llamar()
    {
        JsonNode r = (await (await _client.PostAsJsonAsync("/api/agentes/probar", new { agent = new { name = "C", kind = "codigo", executor = "g" }, input = "x" })).Content.ReadFromJsonAsync<JsonNode>())!;
        Assert.False(r["ok"]!.GetValue<bool>());
        Assert.Contains("propio código", r["error"]!.GetValue<string>());
    }

    private async Task<int> CreateAsync(string url, object body)
    {
        HttpResponseMessage r = await _client.PostAsJsonAsync(url, body);
        string text = await r.Content.ReadAsStringAsync();
        Assert.True(r.IsSuccessStatusCode, text);
        return JsonNode.Parse(text)!["id"]!.GetValue<int>();
    }

    private async Task<int> StartRunAsync(int agentId, string csv)
    {
        using var form = new MultipartFormDataContent { { new ByteArrayContent(Encoding.UTF8.GetBytes(csv)), "archivo", "c.csv" } };
        int datasetId = (await (await _client.PostAsync("/api/datasets/archivo", form)).Content.ReadFromJsonAsync<JsonNode>())!["id"]!.GetValue<int>();
        string[] columns = csv.Split('\n')[0].Split(',');
        var checks = new List<object> { new { type = "latencia", blocking = false } };
        if (columns.Contains("debe_contener"))
        {
            checks.Add(new { type = "contiene", expectedColumn = "debe_contener", blocking = true });
        }
        if (columns.Contains("herramientas_esperadas"))
        {
            checks.Add(new { type = "herramientas", expectedColumn = "herramientas_esperadas", blocking = true });
        }
        int evaluationId = await CreateAsync("/api/evaluaciones", new { name = "E", agentId, datasetId, inputColumn = "input", checks });
        int runId = (await (await _client.PostAsJsonAsync($"/api/evaluaciones/{evaluationId}/ejecutar", new { })).Content.ReadFromJsonAsync<JsonNode>())!["runId"]!.GetValue<int>();
        for (int i = 0; i < 50 && (await RunAsync(runId))["run"]!["status"]!.GetValue<string>() == "Queued"; i++)
        {
            await Task.Delay(50);
        }
        return runId;
    }

    private async Task<JsonNode> RunAsync(int runId) => (await _client.GetFromJsonAsync<JsonNode>($"/api/corridas/{runId}"))!;

    private async Task<JsonArray> ClaimAsync(string group, string mode, int max, int waitSeconds = 0)
    {
        HttpResponseMessage r = await _executor.PostAsJsonAsync("/api/ejecutores/tomar", new { group, worker = "prueba", mode, max, waitSeconds });
        Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
        return (await r.Content.ReadFromJsonAsync<JsonArray>())!;
    }

    private async Task<JsonNode> ReportAsync(object body)
    {
        HttpResponseMessage r = await _executor.PostAsJsonAsync("/api/ejecutores/resultado", body);
        string text = await r.Content.ReadAsStringAsync();
        Assert.True(r.IsSuccessStatusCode, text);
        return JsonNode.Parse(text)!;
    }

    public void Dispose()
    {
        _client.Dispose();
        _executor.Dispose();
        _factory.Dispose();
        SqliteConnection.ClearAllPools();
        File.Delete(_dbPath);
    }
}

public sealed class ExecutorDisabledTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"eval-exec-off-{Guid.NewGuid():N}.db");
    private readonly WebApplicationFactory<Program> _factory;

    public ExecutorDisabledTests()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:Eval", $"Data Source={_dbPath}");
            builder.UseSetting("Demo:CrearEjemplo", "false");
            builder.UseSetting("Ejecutores:Token", "");
        });
    }

    [Fact]
    public async Task Sin_token_configurado_la_api_esta_apagada()
    {
        HttpClient client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Ejecutor-Token", "cualquiera");
        HttpResponseMessage r = await client.PostAsJsonAsync("/api/ejecutores/tomar", new { group = "g" });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, r.StatusCode);
        Assert.False((await client.GetFromJsonAsync<JsonNode>("/api/ejecutores/estado"))!["habilitado"]!.GetValue<bool>());
    }

    public void Dispose()
    {
        _factory.Dispose();
        SqliteConnection.ClearAllPools();
        File.Delete(_dbPath);
    }
}
