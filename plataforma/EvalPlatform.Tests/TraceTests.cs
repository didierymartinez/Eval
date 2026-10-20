using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using EvalPlatform.Services;
using Google.Protobuf;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace EvalPlatform.Tests;

public class OtlpParserTests
{
    // Arma un ExportTraceServiceRequest en protobuf con los mismos números de campo de OTLP.
    public static byte[] Protobuf(string traceId, params (string SpanId, string? Parent, string Name, (string Key, object Value)[] Attrs)[] spans)
    {
        static ByteString Message(Action<CodedOutputStream> write)
        {
            using var ms = new MemoryStream();
            var output = new CodedOutputStream(ms);
            write(output);
            output.Flush();
            return ByteString.CopyFrom(ms.ToArray());
        }

        ByteString scope = Message(o =>
        {
            foreach (var (spanId, parent, name, attrs) in spans)
            {
                o.WriteTag(2, WireFormat.WireType.LengthDelimited);
                o.WriteBytes(Message(s =>
                {
                    s.WriteTag(1, WireFormat.WireType.LengthDelimited); s.WriteBytes(ByteString.CopyFrom(Convert.FromHexString(traceId)));
                    s.WriteTag(2, WireFormat.WireType.LengthDelimited); s.WriteBytes(ByteString.CopyFrom(Convert.FromHexString(spanId)));
                    if (parent is not null)
                    {
                        s.WriteTag(4, WireFormat.WireType.LengthDelimited); s.WriteBytes(ByteString.CopyFrom(Convert.FromHexString(parent)));
                    }
                    s.WriteTag(5, WireFormat.WireType.LengthDelimited); s.WriteString(name);
                    s.WriteTag(7, WireFormat.WireType.Fixed64); s.WriteFixed64(1_000_000_000);
                    s.WriteTag(8, WireFormat.WireType.Fixed64); s.WriteFixed64(1_250_000_000);
                    foreach (var (key, value) in attrs)
                    {
                        s.WriteTag(9, WireFormat.WireType.LengthDelimited);
                        s.WriteBytes(Message(kv =>
                        {
                            kv.WriteTag(1, WireFormat.WireType.LengthDelimited); kv.WriteString(key);
                            kv.WriteTag(2, WireFormat.WireType.LengthDelimited);
                            kv.WriteBytes(Message(any =>
                            {
                                if (value is string str) { any.WriteTag(1, WireFormat.WireType.LengthDelimited); any.WriteString(str); }
                                else { any.WriteTag(3, WireFormat.WireType.Varint); any.WriteInt64(Convert.ToInt64(value)); }
                            }));
                        }));
                    }
                    // Campo desconocido para el parser (status = 15): debe ignorarlo.
                    s.WriteTag(15, WireFormat.WireType.LengthDelimited); s.WriteBytes(ByteString.CopyFromUtf8("x"));
                }));
            }
        });
        ByteString resource = Message(o => { o.WriteTag(2, WireFormat.WireType.LengthDelimited); o.WriteBytes(scope); });
        return Message(o => { o.WriteTag(1, WireFormat.WireType.LengthDelimited); o.WriteBytes(resource); }).ToByteArray();
    }

    public static string Json(string traceId, int input, int output) => $$"""
        { "resourceSpans": [ { "scopeSpans": [ { "spans": [
          { "traceId": "{{traceId}}", "spanId": "1111111111111111", "name": "chat gpt-5", "startTimeUnixNano": "1000000000", "endTimeUnixNano": "1900000000",
            "attributes": [
              { "key": "gen_ai.operation.name", "value": { "stringValue": "chat" } },
              { "key": "gen_ai.response.model", "value": { "stringValue": "gpt-5" } },
              { "key": "gen_ai.usage.input_tokens", "value": { "intValue": "{{input}}" } },
              { "key": "gen_ai.usage.output_tokens", "value": { "intValue": {{output}} } } ] } ] } ] } ] }
        """;

    [Fact]
    public void Protobuf_y_suma_sin_doble_conteo()
    {
        const string trace = "0af7651916cd43dd8448eb211c80319c";
        byte[] data = Protobuf(trace,
            ("aaaaaaaaaaaaaaaa", null, "POST /preguntar", []),
            // Una cadena que ya suma los tokens de sus hijos: no se cuenta.
            ("bbbbbbbbbbbbbbbb", "aaaaaaaaaaaaaaaa", "cadena", [("llm.usage.prompt_tokens", 300L), ("llm.usage.completion_tokens", 60L)]),
            ("cccccccccccccccc", "bbbbbbbbbbbbbbbb", "chat modelo", [("gen_ai.operation.name", "chat"), ("gen_ai.request.model", "m1"), ("gen_ai.usage.input_tokens", 100L), ("gen_ai.usage.output_tokens", 20L)]),
            ("dddddddddddddddd", "bbbbbbbbbbbbbbbb", "chat modelo", [("gen_ai.operation.name", "chat"), ("gen_ai.response.model", "m2"), ("gen_ai.usage.input_tokens", 200L), ("gen_ai.usage.output_tokens", 40L)]),
            ("eeeeeeeeeeeeeeee", "aaaaaaaaaaaaaaaa", "execute_tool buscar", [("gen_ai.operation.name", "execute_tool"), ("gen_ai.tool.name", "buscar")]));

        List<SpanInfo> spans = OtlpTraceParser.ParseProtobuf(data);
        Assert.Equal(5, spans.Count);
        Assert.Equal(trace, spans[0].TraceId);
        Assert.Equal("bbbbbbbbbbbbbbbb", spans[2].ParentSpanId);
        Assert.Equal(250UL, spans[0].EndNanos / 1_000_000 - spans[0].StartNanos / 1_000_000);

        TraceSummary summary = TraceSummary.From(spans);
        Assert.Equal(300, summary.InputTokens);
        Assert.Equal(60, summary.OutputTokens);
        Assert.Equal(360, summary.TotalTokens);
        Assert.Equal(2, summary.ModelCalls);
        Assert.Equal(["m1", "m2"], summary.Models);
        Assert.Contains(summary.Steps, s => s.Tool == "buscar");
        Assert.False(summary.Steps.Single(s => s.Name == "cadena").Counted);
    }

    [Fact]
    public void Json_con_numeros_como_texto_y_ids_en_hexadecimal()
    {
        List<SpanInfo> spans = OtlpTraceParser.ParseJson(JsonDocument.Parse(Json("ABCDEF0123456789ABCDEF0123456789", 120, 30)).RootElement);
        SpanInfo span = Assert.Single(spans);
        Assert.Equal("abcdef0123456789abcdef0123456789", span.TraceId);
        TraceSummary summary = TraceSummary.From(spans);
        Assert.Equal(150, summary.TotalTokens);
        Assert.Equal(900, summary.Steps.Single().DurationMs);
    }

    [Fact]
    public async Task El_almacen_solo_guarda_trazas_registradas_y_espera_los_spans()
    {
        var store = new TraceStore();
        string mine = store.Register();
        string other = TraceStore.NewTraceId();
        Assert.Matches("^00-" + mine + "-[0-9a-f]{16}-01$", TraceStore.TraceParent(mine));

        int accepted = store.Accept(OtlpTraceParser.ParseJson(JsonDocument.Parse(Json(other, 1, 1)).RootElement));
        Assert.Equal(0, accepted);

        // Los spans llegan un poco después de la respuesta, como con un exportador por lotes.
        _ = Task.Run(async () =>
        {
            await Task.Delay(300);
            store.Accept(OtlpTraceParser.ParseJson(JsonDocument.Parse(Json(mine, 10, 5)).RootElement));
        });
        TraceSummary? summary = await store.WaitAsync(mine, TimeSpan.FromSeconds(5), CancellationToken.None, quiet: TimeSpan.FromMilliseconds(200));
        Assert.Equal(15, summary!.TotalTokens);

        // Sin spans: vence el tiempo y no hay resumen.
        string silent = store.Register();
        Assert.Null(await store.WaitAsync(silent, TimeSpan.FromMilliseconds(300), CancellationToken.None));
    }
}

// Un agente que solo devuelve texto y manda sus trazas aparte, como un servicio instrumentado.
public sealed class TraceFlowTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"eval-trace-{Guid.NewGuid():N}.db");
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    public TraceFlowTests()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:Eval", $"Data Source={_dbPath}");
            builder.UseSetting("Demo:CrearEjemplo", "false");
            builder.UseSetting("Ejecutores:Token", "t");
            builder.UseSetting("Trazas:Token", "trazas-secreto");
            builder.ConfigureServices(services =>
            {
                services.AddHttpClient(AgentInvoker.HttpClientName)
                    .ConfigurePrimaryHttpMessageHandler(() => new LazyHandler(() => _factory!.Server.CreateHandler()));
                services.RemoveAll<IJudgeProvider>();
                services.AddSingleton<IJudgeProvider>(new FakeJudgeProvider(null));
                services.AddSingleton<IStartupFilter>(new SilentAgentStartup(() => _factory!));
            });
        });
        _client = _factory.CreateClient();
    }

    [Fact]
    public async Task Sin_token_de_trazas_se_rechaza()
    {
        var content = new StringContent(OtlpParserTests.Json(TraceStore.NewTraceId(), 1, 1), Encoding.UTF8, "application/json");
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, (await _client.PostAsync("/v1/traces", content)).StatusCode);
    }

    [Fact]
    public async Task Tokens_por_trazas_en_una_corrida_y_al_probar()
    {
        var agent = new
        {
            name = "Silencioso",
            url = "http://localhost/agente-silencioso",
            bodyTemplate = """{ "q": "{{input}}" }""",
            responsePath = "respuesta",
            tracesEnabled = true,
            traceWaitSeconds = 10,
        };
        JsonNode test = (await (await _client.PostAsJsonAsync("/api/agentes/probar", new { agent, input = "hola" })).Content.ReadFromJsonAsync<JsonNode>())!;
        Assert.True(test["ok"]!.GetValue<bool>(), test.ToJsonString());
        Assert.Equal(150, test["tokens"]!.GetValue<int>());
        Assert.Equal(1, test["trace"]!["modelCalls"]!.GetValue<int>());

        int agentId = (await (await _client.PostAsJsonAsync("/api/agentes", agent)).Content.ReadFromJsonAsync<JsonNode>())!["id"]!.GetValue<int>();
        using var form = new MultipartFormDataContent { { new ByteArrayContent(Encoding.UTF8.GetBytes("input\nuno\ndos\n")), "archivo", "c.csv" } };
        int datasetId = (await (await _client.PostAsync("/api/datasets/archivo", form)).Content.ReadFromJsonAsync<JsonNode>())!["id"]!.GetValue<int>();
        int evaluationId = (await (await _client.PostAsJsonAsync("/api/evaluaciones", new
        {
            name = "Eficiencia",
            agentId,
            datasetId,
            inputColumn = "input",
            checks = new[] { new { type = "tokens", threshold = 200.0, blocking = true } },
        })).Content.ReadFromJsonAsync<JsonNode>())!["id"]!.GetValue<int>();
        int runId = (await (await _client.PostAsJsonAsync($"/api/evaluaciones/{evaluationId}/ejecutar", new { })).Content.ReadFromJsonAsync<JsonNode>())!["runId"]!.GetValue<int>();

        JsonNode run = await WaitAsync(runId);
        Assert.Equal(2, run["run"]!["passed"]!.GetValue<int>());
        Assert.Equal(150, run["summary"]!["averageTokens"]!.GetValue<double>());
        Assert.Equal(1, run["summary"]!["averageModelCalls"]!.GetValue<double>());

        JsonArray results = (await _client.GetFromJsonAsync<JsonArray>($"/api/corridas/{runId}/resultados"))!;
        Assert.All(results, r => Assert.Equal("gpt-de-prueba", r!["trace"]!["models"]![0]!.GetValue<string>()));
        Assert.NotEqual(results[0]!["traceId"]!.GetValue<string>(), results[1]!["traceId"]!.GetValue<string>());
    }

    [Fact]
    public async Task Tokens_por_trazas_con_ejecutor_remoto()
    {
        int agentId = (await (await _client.PostAsJsonAsync("/api/agentes", new { name = "Remoto", kind = "codigo", executor = "red", tracesEnabled = true, traceWaitSeconds = 10 })).Content.ReadFromJsonAsync<JsonNode>())!["id"]!.GetValue<int>();
        using var form = new MultipartFormDataContent { { new ByteArrayContent(Encoding.UTF8.GetBytes("input\nuno\n")), "archivo", "c.csv" } };
        int datasetId = (await (await _client.PostAsync("/api/datasets/archivo", form)).Content.ReadFromJsonAsync<JsonNode>())!["id"]!.GetValue<int>();
        int evaluationId = (await (await _client.PostAsJsonAsync("/api/evaluaciones", new { name = "R", agentId, datasetId, inputColumn = "input", checks = new[] { new { type = "latencia", blocking = true } } })).Content.ReadFromJsonAsync<JsonNode>())!["id"]!.GetValue<int>();
        int runId = (await (await _client.PostAsJsonAsync($"/api/evaluaciones/{evaluationId}/ejecutar", new { })).Content.ReadFromJsonAsync<JsonNode>())!["runId"]!.GetValue<int>();

        HttpClient executor = _factory.CreateClient();
        executor.DefaultRequestHeaders.Add("X-Ejecutor-Token", "t");
        JsonArray tasks = (await (await executor.PostAsJsonAsync("/api/ejecutores/tomar", new { group = "red", waitSeconds = 5 })).Content.ReadFromJsonAsync<JsonArray>())!;
        string traceId = tasks[0]!["traceId"]!.GetValue<string>();
        Assert.Matches("^[0-9a-f]{32}$", traceId);

        // Tu código llama al agente con traceparent; el agente exporta sus trazas a la plataforma.
        await PostSpansAsync(_client, traceId, 400, 100, delayMs: 400);
        await executor.PostAsJsonAsync("/api/ejecutores/resultado", new { taskId = tasks[0]!["taskId"]!.GetValue<long>(), output = "listo" });

        JsonArray results = (await _client.GetFromJsonAsync<JsonArray>($"/api/corridas/{runId}/resultados"))!;
        Assert.Equal(500, results[0]!["tokens"]!.GetValue<int>());
    }

    internal static async Task PostSpansAsync(HttpClient client, string traceId, int input, int output, int delayMs)
    {
        await Task.Delay(delayMs);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/traces")
        {
            Content = new ByteArrayContent(OtlpParserTests.Protobuf(traceId,
                ("1234567812345678", null, "chat gpt-de-prueba", [("gen_ai.operation.name", "chat"), ("gen_ai.response.model", "gpt-de-prueba"), ("gen_ai.usage.input_tokens", (long)input), ("gen_ai.usage.output_tokens", (long)output)]))),
        };
        request.Content.Headers.ContentType = new("application/x-protobuf");
        request.Headers.Add("X-Trazas-Token", "trazas-secreto");
        (await client.SendAsync(request)).EnsureSuccessStatusCode();
    }

    private async Task<JsonNode> WaitAsync(int runId)
    {
        for (int i = 0; i < 200; i++)
        {
            JsonNode run = (await _client.GetFromJsonAsync<JsonNode>($"/api/corridas/{runId}"))!;
            if (run["run"]!["status"]!.GetValue<string>() is not ("Queued" or "Running"))
            {
                return run;
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

    // Endpoint de prueba: responde solo texto y, aparte, exporta un span con 120 + 30 tokens
    // para la traza que recibió en traceparent.
    private sealed class SilentAgentStartup(Func<WebApplicationFactory<Program>> factory) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Map("/agente-silencioso", a => a.Run(async ctx =>
            {
                string traceId = ctx.Request.Headers["traceparent"].ToString().Split('-')[1];
                _ = Task.Run(() => PostSpansAsync(factory().CreateClient(), traceId, 120, 30, delayMs: 300));
                ctx.Response.ContentType = "application/json";
                await ctx.Response.WriteAsync("""{ "respuesta": "hola" }""");
            }));
            next(app);
        };
    }

    private sealed class LazyHandler(Func<HttpMessageHandler> server) : DelegatingHandler
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
}
