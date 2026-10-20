using System.IO.Compression;
using System.Text.Json;
using EvalPlatform.Services;
using Microsoft.Extensions.Options;

namespace EvalPlatform.Api;

public sealed class TraceOptions
{
    public bool Habilitado { get; set; } = true;

    // Opcional: si se define, los agentes deben mandarlo en X-Trazas-Token o Authorization: Bearer
    // (OTEL_EXPORTER_OTLP_HEADERS=X-Trazas-Token=...).
    public string? Token { get; set; }
}

// Receptor OTLP/HTTP de trazas (POST /v1/traces), en protobuf o JSON y con o sin gzip.
// Configura en el agente OTEL_EXPORTER_OTLP_ENDPOINT=<url de la plataforma>: los exportadores
// agregan /v1/traces solos. Solo se guardan los spans de trazas que inició la plataforma.
// Si el agente no alcanza la plataforma, puede mandarlas a su ejecutor (--trazas), que las reenvía
// con el token de ejecutor.
public static class TraceEndpoints
{
    public static void MapTraceReceiver(this WebApplication app)
    {
        app.MapPost("/v1/traces", async (HttpRequest request, TraceStore store, IOptions<TraceOptions> options, IOptions<ExecutorOptions> executors, ILoggerFactory logs) =>
        {
            TraceOptions o = options.Value;
            if (!o.Habilitado)
            {
                return Results.NotFound();
            }
            if (!string.IsNullOrWhiteSpace(o.Token) && !executors.Value.Accepts(request))
            {
                string? given = request.Headers["X-Trazas-Token"].FirstOrDefault()
                    ?? request.Headers.Authorization.FirstOrDefault()?.Replace("Bearer ", "", StringComparison.OrdinalIgnoreCase);
                if (given?.Trim() != o.Token)
                {
                    return Results.Unauthorized();
                }
            }

            Stream body = request.Body;
            if (request.Headers.ContentEncoding.ToString().Contains("gzip", StringComparison.OrdinalIgnoreCase))
            {
                body = new GZipStream(body, CompressionMode.Decompress);
            }
            using var buffer = new MemoryStream();
            await body.CopyToAsync(buffer);

            bool isJson = request.ContentType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true;
            List<SpanInfo> spans;
            try
            {
                spans = isJson
                    ? OtlpTraceParser.ParseJson(JsonDocument.Parse(buffer.ToArray()).RootElement)
                    : OtlpTraceParser.ParseProtobuf(buffer.ToArray());
            }
            catch (Exception ex) when (ex is JsonException or Google.Protobuf.InvalidProtocolBufferException or InvalidOperationException)
            {
                logs.CreateLogger("Trazas").LogWarning("Pedido OTLP inválido: {Mensaje}", ex.Message);
                return Results.BadRequest();
            }

            store.Accept(spans);
            // ExportTraceServiceResponse vacío: éxito.
            return isJson ? Results.Json(new { }) : Results.Bytes([], "application/x-protobuf");
        }).DisableAntiforgery();
    }
}
