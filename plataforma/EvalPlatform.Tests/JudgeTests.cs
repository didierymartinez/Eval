using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using EvalPlatform.Data;
using EvalPlatform.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace EvalPlatform.Tests;

// Juez de un equipo: siempre da 5 e informa 100 tokens por llamada. Recuerda con qué se creó.
public sealed class TeamJudgeFactory : IJudgeClientFactory
{
    public List<(string? Endpoint, string Model, string? ApiKey)> Created { get; } = [];

    public IChatClient Create(string? endpoint, string model, string? apiKey)
    {
        lock (Created)
        {
            Created.Add((endpoint, model, apiKey));
        }
        return new TeamJudgeClient();
    }

    private sealed class TeamJudgeClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            string prompt = string.Join("\n", messages.Select(m => m.Text));
            string text = prompt.Contains("Vas a crear casos de prueba")
                ? """[{"categoria":"acceso","input":"No puedo entrar","respuesta_esperada":"Espere 30 minutos","herramientas_esperadas":"ninguna","argumentos_esperados":"","debe_contener":"30","tipo":"normal"},{"categoria":"x","input":"b","respuesta_esperada":"c","herramientas_esperadas":"ninguna","argumentos_esperados":"","debe_contener":"","tipo":"normal"},{"categoria":"y","input":"d","respuesta_esperada":"e","herramientas_esperadas":"ninguna","argumentos_esperados":"","debe_contener":"","tipo":"limite"}]"""
                : "<S0>bien</S0><S1>Equivalente según el juez del equipo.</S1><S2>5</S2>";
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, text))
            {
                Usage = new UsageDetails { InputTokenCount = 80, OutputTokenCount = 20, TotalTokenCount = 100 },
            });
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}

public sealed class JudgeTests
{
    private const string Tickets =
        "ticket,referencia\n" +
        "\"No puedo entrar, mi usuario está bloqueado\",Se bloquea por 30 minutos.\n" +
        "¿Cómo anulo una factura electrónica enviada a la DIAN?,Con una nota crédito.\n" +
        "Necesito exportar el reporte de inventario a Excel,Inventario > Reportes > Exportar.\n";

    private static object Evaluation(int agentId, int datasetId, int? judgeId) => new
    {
        name = "Con juez",
        agentId,
        datasetId,
        inputColumn = "ticket",
        judgeId,
        checks = new object[] { new { type = "correccion", expectedColumn = "referencia", blocking = true } },
    };

    [Fact]
    public async Task Cada_equipo_usa_su_juez_y_su_limite()
    {
        var team = new TeamJudgeFactory();
        // El juez del servidor reprueba todo: si se usara, los casos fallarían.
        using var app = new TestApp(new FakeJudgeProvider(new FakeJudgeClient(_ => 1)), team);
        int agentId = await app.CreateHelpdeskAgentAsync();
        int datasetId = await app.UploadAsync("Tickets", Tickets);

        // La clave se guarda cifrada y nunca sale por la API.
        int judgeId = await app.CreateAsync("/api/jueces", new { name = "Soporte", endpoint = "http://modelos.soporte/v1/", model = "gpt-5-mini", apiKey = "sk-secreta-1234" });
        string listing = await app.Client.GetStringAsync("/api/jueces");
        Assert.DoesNotContain("sk-secreta", listing);
        Assert.Contains("…1234", listing);
        await using (EvalDbContext db = await app.Services.GetRequiredService<IDbContextFactory<EvalDbContext>>().CreateDbContextAsync())
        {
            string stored = (await db.Judges.SingleAsync()).ProtectedApiKey!;
            Assert.DoesNotContain("sk-secreta", stored);
        }
        Assert.True(Directory.EnumerateFiles(Path.Combine(app.DataFolder, "claves")).Any());

        // Probar usa la clave guardada sin volver a escribirla.
        JsonNode test = await app.PostJson("/api/jueces/probar", new { id = judgeId });
        Assert.True(test["ok"]!.GetValue<bool>());
        Assert.Contains(team.Created, c => c is { Endpoint: "http://modelos.soporte/v1/", Model: "gpt-5-mini", ApiKey: "sk-secreta-1234" });

        Assert.Contains("no existe", await app.BadRequestAsync("/api/evaluaciones", Evaluation(agentId, datasetId, 999)));
        int evaluationId = await app.CreateAsync("/api/evaluaciones", Evaluation(agentId, datasetId, judgeId));

        JsonNode run = await app.RunAsync(evaluationId);
        Assert.Equal(3, run["run"]!["passed"]!.GetValue<int>());
        Assert.Equal("Soporte", run["run"]!["judgeName"]!.GetValue<string>());
        // 3 casos × 1 comparación × 100 tokens (la prueba de conexión también cuenta).
        Assert.Equal(300, run["summary"]!["judgeTokens"]!.GetValue<long>());
        Assert.All(await app.ResultsAsync(run["run"]!["id"]!.GetValue<int>()), r => Assert.Equal(100, r!["judgeTokens"]!.GetValue<int>()));
        JsonNode judges = (await app.Client.GetFromJsonAsync<JsonNode>("/api/jueces"))!;
        Assert.Equal(400, judges["jueces"]![0]!["tokensMes"]!.GetValue<long>());
        Assert.Equal(0, judges["servidor"]!["tokensMes"]!.GetValue<long>());

        // Generar casos con el juez del equipo también cuenta.
        JsonNode generated = await app.PostJson("/api/datasets/generar", new { name = "Generados", description = "Soporte N1", count = 3, judgeId });
        Assert.Equal(3, generated["rowCount"]!.GetValue<int>());

        // Con el límite superado, las comparaciones con juez quedan como error, sin llamar al modelo.
        HttpResponseMessage update = await app.Client.PutAsJsonAsync($"/api/jueces/{judgeId}", new { name = "Soporte", endpoint = "http://modelos.soporte/v1/", model = "gpt-5-mini", monthlyTokenLimit = 450 });
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        JsonNode limited = await app.RunAsync(evaluationId);
        Assert.Equal(3, limited["run"]!["errors"]!.GetValue<int>());
        JsonArray limitedResults = await app.ResultsAsync(limited["run"]!["id"]!.GetValue<int>());
        Assert.All(limitedResults, r => Assert.Contains("límite mensual", r!["checks"]![0]!["reason"]!.GetValue<string>()));
        // Guardar sin clave conserva la guardada.
        Assert.Contains(team.Created, c => c.ApiKey == "sk-secreta-1234" && c.Model == "gpt-5-mini");

        // La clave guardada nunca viaja a otro endpoint: ni al guardar ni al probar.
        HttpResponseMessage moved = await app.Client.PutAsJsonAsync($"/api/jueces/{judgeId}", new { name = "Soporte", endpoint = "http://otro-servidor/v1/", model = "gpt-5-mini" });
        Assert.Equal(HttpStatusCode.BadRequest, moved.StatusCode);
        Assert.Contains("vuelve a escribir la clave", (await moved.Content.ReadFromJsonAsync<JsonNode>())!["error"]!.GetValue<string>());
        Assert.Contains("escribe la clave", await app.BadRequestAsync("/api/jueces/probar", new { id = judgeId, endpoint = "http://otro-servidor/v1/" }));
        Assert.DoesNotContain(team.Created, c => c.Endpoint == "http://otro-servidor/v1/");
        HttpResponseMessage movedWithKey = await app.Client.PutAsJsonAsync($"/api/jueces/{judgeId}", new { name = "Soporte", endpoint = "http://otro-servidor/v1/", model = "gpt-5-mini", apiKey = "sk-nueva", monthlyTokenLimit = 450 });
        Assert.Equal(HttpStatusCode.OK, movedWithKey.StatusCode);

        // Un juez en uso no se puede borrar.
        Assert.Equal(HttpStatusCode.Conflict, (await app.Client.DeleteAsync($"/api/jueces/{judgeId}")).StatusCode);
    }

    [Fact]
    public async Task El_servidor_puede_prohibir_su_juez_y_limitar_casos()
    {
        using var app = new TestApp(new FakeJudgeProvider(new FakeJudgeClient(_ => 5)), new TeamJudgeFactory(), new()
        {
            ["Juez:PermitirEnEvaluaciones"] = "false",
            ["Limites:CasosPorEjecucion"] = "4",
        });
        int agentId = await app.CreateHelpdeskAgentAsync();
        int datasetId = await app.UploadAsync("Tickets", Tickets);

        JsonNode estado = (await app.Client.GetFromJsonAsync<JsonNode>("/api/estado"))!;
        Assert.False(estado["juezServidorPermitido"]!.GetValue<bool>());

        int withServerJudge = await app.CreateAsync("/api/evaluaciones", Evaluation(agentId, datasetId, null));
        JsonNode run = await app.RunAsync(withServerJudge);
        Assert.Equal(3, run["run"]!["errors"]!.GetValue<int>());
        Assert.Contains("no permite usar su juez", (await app.ResultsAsync(run["run"]!["id"]!.GetValue<int>()))[0]!["checks"]![0]!["reason"]!.GetValue<string>());
        Assert.Contains("Jueces IA", await app.BadRequestAsync("/api/datasets/generar", new { name = "x", description = "y" }));

        // 3 casos × 2 repeticiones = 6 > 4.
        int repeated = await app.CreateAsync("/api/evaluaciones", new
        {
            name = "Repetida",
            agentId,
            datasetId,
            inputColumn = "ticket",
            repetitions = 2,
            checks = new object[] { new { type = "contiene", expectedValue = "a", blocking = true } },
        });
        string error = await app.BadRequestAsync($"/api/evaluaciones/{repeated}/ejecutar", new { });
        Assert.Contains("6 casos", error);
        Assert.Contains("máximo de este servidor es 4", error);
    }
}
