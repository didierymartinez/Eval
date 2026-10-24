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

public class AgentInvokerTests
{
    private sealed class StubHandler(string json) : HttpMessageHandler
    {
        public string? LastBody { get; private set; }
        public string? LastApiKey { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            LastApiKey = request.Headers.TryGetValues("x-api-key", out var v) ? v.Single() : null;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    [Fact]
    public async Task Extrae_respuesta_herramientas_y_suma_tokens_de_la_api_de_anthropic()
    {
        var handler = new StubHandler("""
            { "content": [ { "type": "text", "text": "Hola, " }, { "type": "tool_use", "name": "crear_ticket" }, { "type": "text", "text": "listo." } ],
              "usage": { "input_tokens": 120, "output_tokens": 30 } }
            """);
        var agent = new Agent
        {
            Name = "Claude",
            Url = "https://api.ejemplo/v1/messages",
            Headers = new() { ["x-api-key"] = "{{env:EVAL_TEST_KEY}}" },
            BodyTemplate = """{ "messages": [ { "role": "user", "content": "{{input}}" } ], "metadata": { "caso": "{{id}}" } }""",
            ResponsePath = "content[*].text",
            ToolsPath = "content[*].name",
            TokensPath = "usage.input_tokens, usage.output_tokens",
        };
        Environment.SetEnvironmentVariable("EVAL_TEST_KEY", "k-123");

        AgentInvocation r = await new AgentInvoker(new StubFactory(handler)).InvokeAsync(agent, "Necesito \"ayuda\"", new Dictionary<string, string> { ["id"] = "C-1" }, CancellationToken.None);

        Assert.True(r.Succeeded, r.Error);
        Assert.Equal("Hola, \nlisto.", r.Output);
        Assert.Equal(["crear_ticket"], r.Tools);
        Assert.Equal(150, r.Tokens);
        Assert.Equal("k-123", handler.LastApiKey);
        JsonNode sent = JsonNode.Parse(handler.LastBody!)!;
        Assert.Equal("Necesito \"ayuda\"", sent["messages"]![0]!["content"]!.GetValue<string>());
        Assert.Equal("C-1", sent["metadata"]!["caso"]!.GetValue<string>());
    }
}

public class SchemaUpgradeTests
{
    [Theory]
    [InlineData("RunTasks")]
    [InlineData("Comparisons")]
    [InlineData("Judges")]
    [InlineData("JudgeUsages")]
    public async Task Las_tablas_manuales_coinciden_con_las_de_EF(string table)
    {
        // El nombre de la tabla viene de InlineData, no de una entrada externa.
#pragma warning disable EF1002
        async Task<List<string>> Columns(string path, bool viaUpgrade)
        {
            var options = new DbContextOptionsBuilder<EvalDbContext>().UseSqlite($"Data Source={path}").Options;
            await using var db = new EvalDbContext(options);
            await db.Database.EnsureCreatedAsync();
            if (viaUpgrade)
            {
                await db.Database.ExecuteSqlRawAsync($"""DROP TABLE "{table}";""");
                await SchemaUpgrade.ApplyAsync(db);
            }
            return await db.Database.SqlQueryRaw<string>($"""SELECT name || ' ' || type || ' ' || "notnull" AS "Value" FROM pragma_table_info('{table}') ORDER BY cid""").ToListAsync();
        }
#pragma warning restore EF1002
        string a = Path.Combine(Path.GetTempPath(), $"eval-a-{Guid.NewGuid():N}.db"), b = Path.Combine(Path.GetTempPath(), $"eval-b-{Guid.NewGuid():N}.db");
        try
        {
            Assert.Equal(await Columns(a, false), await Columns(b, true));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(a);
            File.Delete(b);
        }
    }

    [Fact]
    public async Task Agrega_columnas_nuevas_a_una_base_vieja()
    {
        string path = Path.Combine(Path.GetTempPath(), $"eval-schema-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<EvalDbContext>().UseSqlite($"Data Source={path}").Options;
        try
        {
            await using (var db = new EvalDbContext(options))
            {
                await db.Database.EnsureCreatedAsync();
                await db.Database.ExecuteSqlRawAsync("""
                    ALTER TABLE "Runs" DROP COLUMN "Note"; ALTER TABLE "RunResults" DROP COLUMN "Tokens"; ALTER TABLE "RunResults" DROP COLUMN "ToolCalls";
                    ALTER TABLE "Agents" DROP COLUMN "TokensPath"; ALTER TABLE "Agents" DROP COLUMN "ToolArgumentsPath";
                    ALTER TABLE "Agents" DROP COLUMN "Kind"; ALTER TABLE "Agents" DROP COLUMN "Mcp";
                    ALTER TABLE "Agents" DROP COLUMN "Executor"; ALTER TABLE "Runs" DROP COLUMN "Executor";
                    ALTER TABLE "Agents" DROP COLUMN "TracesEnabled"; ALTER TABLE "Agents" DROP COLUMN "TraceWaitSeconds";
                    ALTER TABLE "RunResults" DROP COLUMN "TraceId"; ALTER TABLE "RunResults" DROP COLUMN "Trace";
                    DROP TABLE "RunTasks";
                    DROP INDEX "IX_Runs_ComparisonId";
                    ALTER TABLE "Runs" DROP COLUMN "AgentId"; ALTER TABLE "Runs" DROP COLUMN "Variant";
                    ALTER TABLE "Runs" DROP COLUMN "Parameters"; ALTER TABLE "Runs" DROP COLUMN "ComparisonId";
                    ALTER TABLE "Evaluations" DROP COLUMN "Variants";
                    DROP TABLE "Comparisons";
                    ALTER TABLE "Evaluations" DROP COLUMN "JudgeId"; ALTER TABLE "Runs" DROP COLUMN "JudgeId";
                    ALTER TABLE "Runs" DROP COLUMN "JudgeName"; ALTER TABLE "Runs" DROP COLUMN "ImportedFrom";
                    ALTER TABLE "RunResults" DROP COLUMN "JudgeTokens";
                    ALTER TABLE "Evaluations" DROP COLUMN "RequiresReview"; ALTER TABLE "Runs" DROP COLUMN "ReviewStatus";
                    ALTER TABLE "Runs" DROP COLUMN "Reviewer"; ALTER TABLE "Runs" DROP COLUMN "ReviewedAt";
                    ALTER TABLE "RunResults" DROP COLUMN "ReviewChecks";
                    DROP TABLE "Judges"; DROP TABLE "JudgeUsages";
                    INSERT INTO "Evaluations" ("Name", "AgentId", "DatasetId", "InputColumn", "Checks", "Repetitions", "CreatedAt") VALUES ('vieja', 1, 1, 'input', '[]', 1, 0);
                    INSERT INTO "Agents" ("Name", "Url", "Method", "Headers", "BodyTemplate", "ResponsePath", "TimeoutSeconds", "CreatedAt") VALUES ('viejo', 'http://x', 'POST', '{{}}', '', '', 10, 0);
                    """);
            }

            await using (var db = new EvalDbContext(options))
            {
                await SchemaUpgrade.ApplyAsync(db);
                await SchemaUpgrade.ApplyAsync(db); // idempotente
                db.Runs.Add(new Run { EvaluationName = "e", Note = "prompt v2" });
                await db.SaveChangesAsync();
                Assert.Equal("prompt v2", (await db.Runs.SingleAsync()).Note);
                // Un agente creado antes del soporte MCP queda como agente HTTP.
                Agent viejo = await db.Agents.SingleAsync();
                Assert.Equal(AgentKinds.Http, viejo.Kind);
                Assert.Equal("{{herramienta}}", viejo.Mcp.ToolTemplate);
                Assert.False(viejo.TracesEnabled);
                Assert.Equal(15, viejo.TraceWaitSeconds);
                db.RunTasks.Add(new RunTask { RunId = 1, Group = "g", Input = "x", Row = new() { ["a"] = "b" }, LeaseUntil = DateTimeOffset.UtcNow });
                await db.SaveChangesAsync();
                Assert.Equal("b", (await db.RunTasks.SingleAsync()).Row["a"]);
                // Evaluaciones y corridas anteriores a las variantes.
                Evaluation vieja = await db.Evaluations.SingleAsync();
                Assert.Empty(vieja.Variants);
                Run corrida = await db.Runs.SingleAsync();
                Assert.Null(corrida.Variant);
                Assert.Empty(corrida.Parameters);
                db.Comparisons.Add(new Comparison { EvaluationId = vieja.Id, EvaluationName = "vieja" });
                await db.SaveChangesAsync();
                // Antes de los jueces por equipo: el juez del servidor.
                Assert.Null(vieja.JudgeId);
                Assert.Null(corrida.JudgeName);
                db.Judges.Add(new Judge { Name = "equipo", Model = "m" });
                db.JudgeUsages.Add(new JudgeUsage { JudgeKey = "servidor", Month = "2026-10", Tokens = 5 });
                await db.SaveChangesAsync();
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(path);
        }
    }
}

public sealed class GenerationAndNotesTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"eval-gen-{Guid.NewGuid():N}.db");
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    public GenerationAndNotesTests()
    {
        // El "modelo" devuelve casos con texto alrededor y bloque ```json, como suelen hacerlo.
        const string generated = """
            Aquí tienes los casos:
            ```json
            [
              { "categoria": "acceso", "input": "Se me bloqueó el usuario", "respuesta_esperada": "Espere 30 minutos o use Olvidé mi contraseña.", "herramientas_esperadas": "base_conocimiento", "debe_contener": "30 minutos", "tipo": "normal" },
              { "categoria": "fuera", "input": "¿Me recomiendas una película?", "respuesta_esperada": "Debe decir que solo atiende soporte.", "herramientas_esperadas": "ninguna", "debe_contener": "", "tipo": "fuera_de_alcance" },
              { "categoria": "x", "input": "" }
            ]
            ```
            """;
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:Eval", $"Data Source={_dbPath}");
            builder.UseSetting("Demo:CrearEjemplo", "false");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IJudgeProvider>();
                services.AddSingleton<IJudgeProvider>(new FakeJudgeProvider(new FakeJudgeClient(p => p.Contains("casos de prueba") ? generated : "<S1>ok</S1><S2>5</S2>")));
            });
        });
        _client = _factory.CreateClient();
    }

    [Fact]
    public async Task Genera_casos_con_ia_y_los_descarga_en_excel()
    {
        HttpResponseMessage response = await _client.PostAsJsonAsync("/api/datasets/generar", new { name = "Borrador", description = "Agente de soporte", tools = "base_conocimiento", count = 5 });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        JsonNode created = (await response.Content.ReadFromJsonAsync<JsonNode>())!;
        Assert.Equal(2, created["rowCount"]!.GetValue<int>());

        JsonNode dataset = (await _client.GetFromJsonAsync<JsonNode>($"/api/datasets/{created["id"]}"))!;
        Assert.Equal("Se me bloqueó el usuario", dataset["rows"]![0]!["input"]!.GetValue<string>());
        Assert.Equal("G-002", dataset["rows"]![1]!["id"]!.GetValue<string>());

        HttpResponseMessage excel = await _client.GetAsync($"/api/datasets/{created["id"]}/excel");
        using var workbook = new ClosedXML.Excel.XLWorkbook(await excel.Content.ReadAsStreamAsync());
        Assert.Equal("input", workbook.Worksheet(1).Cell(1, 3).GetString());
        Assert.Equal("¿Me recomiendas una película?", workbook.Worksheet(1).Cell(3, 3).GetString());
    }

    [Fact]
    public async Task La_condicion_y_las_instrucciones_del_juez_quedan_guardadas()
    {
        int agentId = (await (await _client.PostAsJsonAsync("/api/agentes", new { name = "A", url = "http://localhost/demo/helpdesk", bodyTemplate = """{ "mensaje": "{{input}}" }""", responsePath = "output.respuesta" })).Content.ReadFromJsonAsync<JsonNode>())!["id"]!.GetValue<int>();
        using var form = new MultipartFormDataContent { { new ByteArrayContent(Encoding.UTF8.GetBytes("input,referencia\nhola,adiós\n")), "archivo", "c.csv" } };
        int datasetId = (await (await _client.PostAsync("/api/datasets/archivo", form)).Content.ReadFromJsonAsync<JsonNode>())!["id"]!.GetValue<int>();
        int evaluationId = (await (await _client.PostAsJsonAsync("/api/evaluaciones", new
        {
            name = "E", agentId, datasetId, inputColumn = "input",
            checks = new object[]
            {
                new { type = "correccion", expectedColumn = "referencia", blocking = true, onlyWhenPath = " output.puede_responder ", onlyWhenValue = "sí", judgeInstructions = "  No exijas fechas.  " },
                // En una comparación sin juez las instrucciones no se guardan.
                new { type = "contiene", expectedColumn = "referencia", blocking = true, judgeInstructions = "x" },
            },
        })).Content.ReadFromJsonAsync<JsonNode>())!["id"]!.GetValue<int>();

        JsonNode checks = (await _client.GetFromJsonAsync<JsonNode>($"/api/evaluaciones/{evaluationId}"))!["evaluation"]!["checks"]!;
        Assert.Equal("output.puede_responder", checks[0]!["onlyWhenPath"]!.GetValue<string>());
        Assert.Equal("sí", checks[0]!["onlyWhenValue"]!.GetValue<string>());
        Assert.Equal("No exijas fechas.", checks[0]!["judgeInstructions"]!.GetValue<string>());
        Assert.Null(checks[1]!["judgeInstructions"]);
        Assert.Null(checks[1]!["onlyWhenPath"]);
    }

    [Fact]
    public async Task La_nota_de_la_corrida_queda_guardada()
    {
        int agentId = (await (await _client.PostAsJsonAsync("/api/agentes", new { name = "A", url = "http://localhost/demo/helpdesk", bodyTemplate = """{ "mensaje": "{{input}}" }""", responsePath = "output.respuesta" })).Content.ReadFromJsonAsync<JsonNode>())!["id"]!.GetValue<int>();
        using var form = new MultipartFormDataContent { { new ByteArrayContent(Encoding.UTF8.GetBytes("input\nhola\n")), "archivo", "c.csv" } };
        int datasetId = (await (await _client.PostAsync("/api/datasets/archivo", form)).Content.ReadFromJsonAsync<JsonNode>())!["id"]!.GetValue<int>();
        int evaluationId = (await (await _client.PostAsJsonAsync("/api/evaluaciones", new { name = "E", agentId, datasetId, inputColumn = "input", checks = new[] { new { type = "latencia", expectedColumn = "input", blocking = true } } })).Content.ReadFromJsonAsync<JsonNode>())!["id"]!.GetValue<int>();

        // "Responde a tiempo" no usa valor esperado: no se guarda aunque llegue.
        JsonNode saved = (await _client.GetFromJsonAsync<JsonNode>($"/api/evaluaciones/{evaluationId}"))!;
        Assert.Null(saved["evaluation"]!["checks"]![0]!["expectedColumn"]);

        JsonNode run = (await (await _client.PostAsJsonAsync($"/api/evaluaciones/{evaluationId}/ejecutar", new { note = "  prompt v2 con árbol de decisión " })).Content.ReadFromJsonAsync<JsonNode>())!;
        JsonNode detail = (await _client.GetFromJsonAsync<JsonNode>($"/api/corridas/{run["runId"]}"))!;
        Assert.Equal("prompt v2 con árbol de decisión", detail["run"]!["note"]!.GetValue<string>());

        // Sin cuerpo también funciona (nota vacía).
        HttpResponseMessage noBody = await _client.PostAsync($"/api/evaluaciones/{evaluationId}/ejecutar", null);
        Assert.Equal(HttpStatusCode.Accepted, noBody.StatusCode);
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
        SqliteConnection.ClearAllPools();
        File.Delete(_dbPath);
    }
}
