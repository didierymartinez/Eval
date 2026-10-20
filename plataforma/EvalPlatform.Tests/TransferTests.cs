using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;

namespace EvalPlatform.Tests;

// Llevar resultados de una plataforma a otra (de una red aislada a la central) y trazas por el ejecutor.
public sealed class TransferTests
{
    private const string Tickets =
        "ticket,debe_responder\n" +
        "\"No puedo entrar, mi usuario está bloqueado\",si\n" +
        "¿Cómo creo un usuario nuevo que solo pueda consultar y no modificar nada?,si\n";

    private static object Evaluation(int agentId, int datasetId) => new
    {
        name = "Helpdesk aislado",
        agentId,
        datasetId,
        inputColumn = "ticket",
        checks = new object[] { new { type = "igual", label = "Decisión", expectedColumn = "debe_responder", responsePath = "output.puede_responder", blocking = true } },
        variants = new object[]
        {
            new { name = "Actual", parameters = new Dictionary<string, string> { ["base"] = "actual" } },
            new { name = "Ampliada", parameters = new Dictionary<string, string> { ["base"] = "ampliada" } },
        },
    };

    [Fact]
    public async Task Exporta_en_una_plataforma_e_importa_en_otra()
    {
        using var isolated = new TestApp(settings: new() { ["Plataforma:Nombre"] = "red-planta" });
        int agentId = await isolated.CreateHelpdeskAgentAsync();
        int datasetId = await isolated.UploadAsync("Tickets planta", Tickets);
        int evaluationId = await isolated.CreateAsync("/api/evaluaciones", Evaluation(agentId, datasetId));
        JsonNode run = await isolated.RunAsync(evaluationId, new { note = "antes del manual" });
        int runId = run["run"]!["id"]!.GetValue<int>();
        int comparisonId = (await isolated.PostJson($"/api/evaluaciones/{evaluationId}/comparar", new { note = "¿sirve el manual?" }))["comparisonId"]!.GetValue<int>();
        for (int i = 0; i < 150 && !(await isolated.Client.GetFromJsonAsync<JsonNode>($"/api/comparativas/{comparisonId}"))!["report"]!["finished"]!.GetValue<bool>(); i++)
        {
            await Task.Delay(100);
        }

        byte[] runFile = await isolated.Client.GetByteArrayAsync($"/api/corridas/{runId}/exportar");
        byte[] comparisonFile = await isolated.Client.GetByteArrayAsync($"/api/comparativas/{comparisonId}/exportar");
        JsonNode exported = JsonNode.Parse(comparisonFile)!;
        Assert.Equal("eval-resultados", exported["formato"]!.GetValue<string>());
        Assert.Equal("red-planta", exported["origen"]!.GetValue<string>());
        Assert.Equal(2, exported["corridas"]!.AsArray().Count);

        using var central = new TestApp();

        // Un archivo que no es una exportación se rechaza con un mensaje claro.
        string bad = await ImportErrorAsync(central, Encoding.UTF8.GetBytes("""{ "hola": 1 }"""));
        Assert.Contains("no es una exportación", bad);

        // Sin evaluación de destino: se crean la evaluación y el dataset con los casos.
        JsonNode imported = await ImportAsync(central, runFile, null);
        int importedRun = imported["runIds"]![0]!.GetValue<int>();
        JsonNode copy = (await central.Client.GetFromJsonAsync<JsonNode>($"/api/corridas/{importedRun}"))!;
        Assert.Equal("Completed", copy["run"]!["status"]!.GetValue<string>());
        Assert.Equal("antes del manual", copy["run"]!["note"]!.GetValue<string>());
        Assert.StartsWith("red-planta · exportada el", copy["run"]!["importedFrom"]!.GetValue<string>());
        Assert.Equal(run["run"]!["passed"]!.GetValue<int>(), copy["run"]!["passed"]!.GetValue<int>());
        JsonArray original = await isolated.ResultsAsync(runId);
        JsonArray results = await central.ResultsAsync(importedRun);
        Assert.Equal(original.Select(r => r!["output"]!.GetValue<string>()), results.Select(r => r!["output"]!.GetValue<string>()));
        Assert.Equal(original.Select(r => r!["checks"]![0]!["reason"]!.GetValue<string>()), results.Select(r => r!["checks"]![0]!["reason"]!.GetValue<string>()));

        int newEvaluation = imported["evaluationId"]!.GetValue<int>();
        JsonNode evaluation = (await central.Client.GetFromJsonAsync<JsonNode>($"/api/evaluaciones/{newEvaluation}"))!;
        Assert.Equal("Helpdesk aislado", evaluation["evaluation"]!["name"]!.GetValue<string>());
        JsonArray datasets = (await central.Client.GetFromJsonAsync<JsonArray>("/api/datasets"))!;
        Assert.Equal("Tickets planta (importado)", datasets.Single()!["name"]!.GetValue<string>());
        Assert.Equal(2, datasets.Single()!["rowCount"]!.GetValue<int>());

        // La comparativa, a la misma evaluación: queda lado a lado igual que en el origen.
        JsonNode importedComparison = await ImportAsync(central, comparisonFile, newEvaluation);
        Assert.Equal(newEvaluation, importedComparison["evaluationId"]!.GetValue<int>());
        int copyId = importedComparison["comparisonId"]!.GetValue<int>();
        JsonNode report = (await central.Client.GetFromJsonAsync<JsonNode>($"/api/comparativas/{copyId}"))!["report"]!;
        Assert.Equal(["Actual", "Ampliada"], report["variants"]!.AsArray().Select(v => v!["name"]!.GetValue<string>()));
        Assert.Contains("«Ampliada»", report["verdict"]!.GetValue<string>());
        Assert.Single((await central.Client.GetFromJsonAsync<JsonNode>($"/api/evaluaciones/{newEvaluation}"))!["comparisons"]!.AsArray());
    }

    [Fact]
    public async Task Las_trazas_que_reenvia_un_ejecutor_entran_con_su_token()
    {
        using var app = new TestApp(settings: new() { ["Trazas:Token"] = "trazas", ["Ejecutores:Token"] = "ejecutor" });
        async Task<HttpStatusCode> PostAsync(string? header, string? value)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/traces") { Content = new StringContent("""{ "resourceSpans": [] }""", Encoding.UTF8, "application/json") };
            if (header is not null)
            {
                request.Headers.Add(header, value);
            }
            return (await app.Client.SendAsync(request)).StatusCode;
        }
        Assert.Equal(HttpStatusCode.Unauthorized, await PostAsync(null, null));
        Assert.Equal(HttpStatusCode.Unauthorized, await PostAsync("X-Ejecutor-Token", "otro"));
        Assert.Equal(HttpStatusCode.OK, await PostAsync("X-Trazas-Token", "trazas"));
        Assert.Equal(HttpStatusCode.OK, await PostAsync("X-Ejecutor-Token", "ejecutor"));
    }

    private static async Task<JsonNode> ImportAsync(TestApp app, byte[] file, int? evaluationId)
    {
        using var form = new MultipartFormDataContent { { new ByteArrayContent(file), "archivo", "resultados.json" } };
        if (evaluationId is int id)
        {
            form.Add(new StringContent(id.ToString()), "evaluacion");
        }
        HttpResponseMessage response = await app.Client.PostAsync("/api/importar", form);
        string text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, text);
        return JsonNode.Parse(text)!;
    }

    private static async Task<string> ImportErrorAsync(TestApp app, byte[] file)
    {
        using var form = new MultipartFormDataContent { { new ByteArrayContent(file), "archivo", "x.json" } };
        HttpResponseMessage response = await app.Client.PostAsync("/api/importar", form);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonNode>())!["error"]!.GetValue<string>();
    }
}
