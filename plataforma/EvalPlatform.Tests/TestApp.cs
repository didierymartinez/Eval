using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using EvalPlatform.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace EvalPlatform.Tests;

// Una plataforma en memoria con su propia base, para las pruebas que necesitan varias a la vez
// (por ejemplo, exportar de una e importar en otra). Los agentes se llaman al mismo servidor de pruebas.
public sealed class TestApp : IDisposable
{
    // Carpeta propia: ahí quedan la base y las llaves de cifrado (claves/).
    private readonly string _folder = Directory.CreateTempSubdirectory("eval-app-").FullName;
    private readonly WebApplicationFactory<Program> _factory;

    public TestApp(IJudgeProvider? serverJudge = null, IJudgeClientFactory? teamJudges = null, Dictionary<string, string>? settings = null)
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:Eval", $"Data Source={Path.Combine(_folder, "eval.db")}");
            builder.UseSetting("Demo:CrearEjemplo", "false");
            foreach (var (key, value) in settings ?? [])
            {
                builder.UseSetting(key, value);
            }
            builder.ConfigureServices(services =>
            {
                services.AddHttpClient(AgentInvoker.HttpClientName)
                    .ConfigurePrimaryHttpMessageHandler(() => new DelegatingToServer(() => _factory!.Server.CreateHandler()));
                services.RemoveAll<IJudgeProvider>();
                services.AddSingleton(serverJudge ?? new FakeJudgeProvider(null));
                if (teamJudges is not null)
                {
                    services.RemoveAll<IJudgeClientFactory>();
                    services.AddSingleton(teamJudges);
                }
            });
        });
        Client = _factory.CreateClient();
    }

    public HttpClient Client { get; }

    public IServiceProvider Services => _factory.Services;

    public string DataFolder => _folder;

    public HttpClient CreateClient() => _factory.CreateClient();

    public async Task<JsonNode> PostJson(string url, object body)
    {
        HttpResponseMessage response = await Client.PostAsJsonAsync(url, body);
        string text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{url}: {(int)response.StatusCode} {text}");
        return JsonNode.Parse(text)!;
    }

    public async Task<int> CreateAsync(string url, object body) => (await PostJson(url, body))["id"]!.GetValue<int>();

    public async Task<string> BadRequestAsync(string url, object body)
    {
        HttpResponseMessage response = await Client.PostAsJsonAsync(url, body);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonNode>())!["error"]!.GetValue<string>();
    }

    public async Task<int> UploadAsync(string name, string csv)
    {
        using var form = new MultipartFormDataContent
        {
            { new StringContent(name), "nombre" },
            { new ByteArrayContent(Encoding.UTF8.GetBytes(csv)), "archivo", "casos.csv" },
        };
        HttpResponseMessage upload = await Client.PostAsync("/api/datasets/archivo", form);
        Assert.Equal(HttpStatusCode.Created, upload.StatusCode);
        return (await upload.Content.ReadFromJsonAsync<JsonNode>())!["id"]!.GetValue<int>();
    }

    // El agente simulado de helpdesk de la demo (responde en /demo/helpdesk).
    public Task<int> CreateHelpdeskAgentAsync(string name = "Helpdesk") => CreateAsync("/api/agentes", new
    {
        name,
        url = "http://localhost/demo/helpdesk",
        bodyTemplate = """{ "mensaje": "{{input}}", "base": "{{base}}" }""",
        responsePath = "output.respuesta",
        contextPath = "intermediateSteps[*].observation",
        timeoutSeconds = 30,
    });

    public async Task<JsonNode> RunAsync(int evaluationId, object? body = null)
    {
        int runId = (await PostJson($"/api/evaluaciones/{evaluationId}/ejecutar", body ?? new { }))["runId"]!.GetValue<int>();
        return await WaitForRunAsync(runId);
    }

    public async Task<JsonNode> WaitForRunAsync(int runId)
    {
        for (int i = 0; i < 150; i++)
        {
            JsonNode run = (await Client.GetFromJsonAsync<JsonNode>($"/api/corridas/{runId}"))!;
            if (run["run"]!["status"]!.GetValue<string>() is not ("Queued" or "Running"))
            {
                return run;
            }
            await Task.Delay(100);
        }
        throw new TimeoutException($"La corrida {runId} no terminó.");
    }

    public async Task<JsonArray> ResultsAsync(int runId) => (await Client.GetFromJsonAsync<JsonArray>($"/api/corridas/{runId}/resultados"))!;

    public void Dispose()
    {
        Client.Dispose();
        _factory.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_folder, recursive: true);
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
