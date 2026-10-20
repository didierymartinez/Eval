using System.Net.Http.Json;
using System.Text.Json;
using EvalPlatform.Domain;
using Microsoft.Extensions.Options;

namespace EvalPlatform.Services;

// Modo ejecutor: el mismo programa, desplegado dentro de la red donde vive el agente.
//   EvalPlatform ejecutor --plataforma https://evaluaciones.miempresa --grupo red-finanzas
// Pide casos a la plataforma (solo conexiones salientes), llama al agente HTTP o MCP desde
// su red y devuelve la respuesta. La calificación (y el juez IA) se hace en la plataforma.
// Las variables {{env:...}} de los agentes se leen aquí, así que los secretos quedan en esa red.
public static class ExecutorWorker
{
    public static async Task<int> RunAsync(string[] args)
    {
        var options = Parse(args);
        if (options is null)
        {
            Console.Error.WriteLine("""
                Uso: EvalPlatform ejecutor --plataforma <url> --grupo <grupo> [--nombre <nombre>] [--concurrencia <n>] [--trazas <puerto>]
                El token se lee de la variable de entorno EVAL_EJECUTOR_TOKEN (o --token).
                --trazas 4318 recibe las trazas OpenTelemetry de los agentes de esta red y las reenvía a la plataforma.
                """);
            return 2;
        }

        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddHttpClient(AgentInvoker.HttpClientName, c => c.Timeout = Timeout.InfiniteTimeSpan);
        builder.Services.AddHttpClient("plataforma", c =>
        {
            c.BaseAddress = new Uri(options.Platform.TrimEnd('/') + "/");
            c.Timeout = TimeSpan.FromSeconds(60);
            c.DefaultRequestHeaders.Add("X-Ejecutor-Token", options.Token);
            c.DefaultRequestHeaders.Add("X-Ejecutor-Nombre", options.Name);
        });
        builder.Services.Configure<JudgeOptions>(builder.Configuration.GetSection("Juez"));
        builder.Services.AddSingleton<IJudgeProvider, OpenAiCompatibleJudgeProvider>();
        builder.Services.AddSingleton<AgentInvoker>();
        builder.Services.AddSingleton<IAgentModelFactory, OpenAiCompatibleModelFactory>();
        builder.Services.AddSingleton<McpInvoker>();
        builder.Services.AddSingleton<AgentDispatcher>();
        using IHost host = builder.Build();

        var log = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Ejecutor");
        var http = host.Services.GetRequiredService<IHttpClientFactory>();
        var dispatcher = host.Services.GetRequiredService<AgentDispatcher>();

        using var stop = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => stop.Cancel();

        log.LogInformation("Ejecutor {Nombre} del grupo {Grupo} conectado a {Plataforma} ({Concurrencia} en paralelo).", options.Name, options.Group, options.Platform, options.Concurrency);
        WebApplication? relay = options.TracePort is int port ? await StartTraceRelayAsync(port, http, log, stop.Token) : null;
        await Task.WhenAll(Enumerable.Range(0, options.Concurrency).Select(_ => LoopAsync(options, http, dispatcher, log, stop.Token)));
        if (relay is not null)
        {
            await relay.StopAsync();
        }
        return 0;
    }

    // Recibe OTLP/HTTP (POST /v1/traces) de los agentes de esta red y lo reenvía a la plataforma
    // por la misma salida que usa el ejecutor. Los agentes usan OTEL_EXPORTER_OTLP_ENDPOINT=http://<ejecutor>:<puerto>.
    private static async Task<WebApplication> StartTraceRelayAsync(int port, IHttpClientFactory http, ILogger log, CancellationToken ct)
    {
        WebApplicationBuilder web = WebApplication.CreateSlimBuilder();
        web.WebHost.UseUrls($"http://0.0.0.0:{port}");
        web.Logging.SetMinimumLevel(LogLevel.Warning);
        WebApplication app = web.Build();
        app.MapPost("/v1/traces", async (HttpRequest request, CancellationToken requestAborted) =>
        {
            using var content = new StreamContent(request.Body);
            content.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse(request.ContentType ?? "application/x-protobuf");
            foreach (string? encoding in request.Headers.ContentEncoding)
            {
                if (!string.IsNullOrEmpty(encoding))
                {
                    content.Headers.ContentEncoding.Add(encoding);
                }
            }
            try
            {
                using HttpResponseMessage response = await http.CreateClient("plataforma").PostAsync("v1/traces", content, requestAborted);
                byte[] body = await response.Content.ReadAsByteArrayAsync(requestAborted);
                if (!response.IsSuccessStatusCode)
                {
                    log.LogWarning("La plataforma rechazó trazas: HTTP {Estado}.", (int)response.StatusCode);
                    return Results.StatusCode((int)response.StatusCode);
                }
                return Results.Bytes(body, response.Content.Headers.ContentType?.ToString());
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                // 503: el exportador de OpenTelemetry reintenta solo.
                log.LogWarning("No se pudieron reenviar trazas a la plataforma: {Mensaje}", ex.Message);
                return Results.StatusCode(503);
            }
        });
        await app.StartAsync(ct);
        log.LogInformation("Recibiendo trazas OpenTelemetry en http://0.0.0.0:{Puerto}/v1/traces y reenviándolas a la plataforma.", port);
        return app;
    }

    private static async Task LoopAsync(Options options, IHttpClientFactory http, AgentDispatcher dispatcher, ILogger log, CancellationToken ct)
    {
        TimeSpan backoff = TimeSpan.FromSeconds(2);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                HttpClient platform = http.CreateClient("plataforma");
                HttpResponseMessage claim = await platform.PostAsJsonAsync("api/ejecutores/tomar",
                    new { group = options.Group, worker = options.Name, mode = "generico", max = 1, waitSeconds = 20 }, ct);
                if (!claim.IsSuccessStatusCode)
                {
                    throw new HttpRequestException($"La plataforma respondió {(int)claim.StatusCode}: {await claim.Content.ReadAsStringAsync(ct)}");
                }
                List<ExecutorTask> tasks = (await claim.Content.ReadFromJsonAsync<List<ExecutorTask>>(JsonSerializerOptions.Web, ct))!;
                backoff = TimeSpan.FromSeconds(2);

                foreach (ExecutorTask task in tasks)
                {
                    AgentInvocation r = await dispatcher.InvokeAsync(task.Agent, task.Input, Variant.Merge(task.Row, task.Parameters), ct, task.TraceId);
                    var result = new ExecutorResult(
                        task.TaskId,
                        r.Output,
                        r.ToolCalls.Select(c => new ExecutorToolCall(c.Name, c.Arguments is null ? null : JsonDocument.Parse(JsonSerializer.Serialize(c.Arguments)).RootElement, c.Result)).ToList(),
                        r.Context,
                        r.Tokens,
                        r.LatencyMs,
                        r.Error,
                        r.RawResponse,
                        r.HttpStatus);
                    HttpResponseMessage sent = await platform.PostAsJsonAsync("api/ejecutores/resultado", result, JsonSerializerOptions.Web, ct);
                    log.LogInformation("Corrida {Run} · caso {Caso}: {Estado}", task.RunId, task.CaseNumber,
                        sent.IsSuccessStatusCode ? (await sent.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("resultado").GetString() : $"no se pudo enviar ({(int)sent.StatusCode})");
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
            {
                log.LogWarning("No se pudo hablar con la plataforma: {Mensaje}. Reintento en {Segundos} s.", ex.Message, backoff.TotalSeconds);
                await Task.Delay(backoff, ct).ContinueWith(_ => { });
                backoff = TimeSpan.FromSeconds(Math.Min(backoff.TotalSeconds * 2, 60));
            }
        }
    }

    private sealed record Options(string Platform, string Group, string Name, string Token, int Concurrency, int? TracePort);

    private static Options? Parse(string[] args)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i + 1 < args.Length; i += 2)
        {
            if (args[i].StartsWith("--", StringComparison.Ordinal))
            {
                map[args[i][2..]] = args[i + 1];
            }
        }
        string? platform = map.GetValueOrDefault("plataforma") ?? Environment.GetEnvironmentVariable("EVAL_PLATAFORMA");
        string? group = map.GetValueOrDefault("grupo") ?? Environment.GetEnvironmentVariable("EVAL_GRUPO");
        string? token = map.GetValueOrDefault("token") ?? Environment.GetEnvironmentVariable("EVAL_EJECUTOR_TOKEN");
        if (string.IsNullOrWhiteSpace(platform) || string.IsNullOrWhiteSpace(group) || string.IsNullOrWhiteSpace(token))
        {
            return null;
        }
        string name = map.GetValueOrDefault("nombre") ?? Environment.GetEnvironmentVariable("EVAL_NOMBRE") ?? Environment.MachineName;
        int concurrency = int.TryParse(map.GetValueOrDefault("concurrencia") ?? Environment.GetEnvironmentVariable("EVAL_CONCURRENCIA"), out int c) ? Math.Clamp(c, 1, 20) : 2;
        int? tracePort = int.TryParse(map.GetValueOrDefault("trazas") ?? Environment.GetEnvironmentVariable("EVAL_PUERTO_TRAZAS"), out int p) && p is > 0 and < 65536 ? p : null;
        return new Options(platform, group.Trim().ToLowerInvariant(), name, token, concurrency, tracePort);
    }
}
