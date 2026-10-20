using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Google.Protobuf;

namespace EvalPlatform.Services;

// Un span de OpenTelemetry, con lo necesario para medir tokens y pasos del agente.
public sealed record SpanInfo(
    string TraceId,
    string SpanId,
    string? ParentSpanId,
    string Name,
    ulong StartNanos,
    ulong EndNanos,
    Dictionary<string, object?> Attributes);

// Un paso del agente según sus trazas: una llamada al modelo, a una herramienta, etc.
public sealed class TraceStep
{
    public string Name { get; set; } = "";
    public string? Operation { get; set; }
    public string? Model { get; set; }
    public string? Tool { get; set; }
    public int? InputTokens { get; set; }
    public int? OutputTokens { get; set; }
    public double DurationMs { get; set; }
    public bool Counted { get; set; }
}

// Lo que se sabe de un caso por sus trazas.
public sealed class TraceSummary
{
    public int Spans { get; set; }
    public int? InputTokens { get; set; }
    public int? OutputTokens { get; set; }
    public int ModelCalls { get; set; }
    public List<string> Models { get; set; } = [];
    public List<TraceStep> Steps { get; set; } = [];

    public int? TotalTokens => InputTokens is null && OutputTokens is null ? null : (InputTokens ?? 0) + (OutputTokens ?? 0);

    // Convenciones GenAI de OpenTelemetry, y nombres anteriores o de otras librerías.
    private static readonly string[] s_input = ["gen_ai.usage.input_tokens", "gen_ai.usage.prompt_tokens", "llm.usage.prompt_tokens", "llm.token_count.prompt"];
    private static readonly string[] s_output = ["gen_ai.usage.output_tokens", "gen_ai.usage.completion_tokens", "llm.usage.completion_tokens", "llm.token_count.completion"];

    public static TraceSummary From(IReadOnlyCollection<SpanInfo> spans)
    {
        var steps = spans
            .OrderBy(s => s.StartNanos)
            .Select(s => (Span: s, Step: new TraceStep
            {
                Name = s.Name,
                Operation = Text(s, "gen_ai.operation.name"),
                Model = Text(s, "gen_ai.response.model") ?? Text(s, "gen_ai.request.model"),
                Tool = Text(s, "gen_ai.tool.name"),
                InputTokens = Number(s, s_input),
                OutputTokens = Number(s, s_output),
                DurationMs = s.EndNanos > s.StartNanos ? (s.EndNanos - s.StartNanos) / 1_000_000.0 : 0,
            }))
            .ToList();

        // Si un span con tokens tiene descendientes que también los tienen, sus tokens ya
        // están en los de ellos (p. ej. una cadena que suma sus llamadas): no se cuentan dos veces.
        var parentOf = spans.Where(s => s.ParentSpanId is not null).GroupBy(s => s.SpanId).ToDictionary(g => g.Key, g => g.First().ParentSpanId!);
        var ancestorsOfUsage = new HashSet<string>();
        foreach (var (span, step) in steps.Where(x => x.Step.InputTokens is not null || x.Step.OutputTokens is not null))
        {
            for (string? p = parentOf.GetValueOrDefault(span.SpanId); p is not null && ancestorsOfUsage.Add(p); p = parentOf.GetValueOrDefault(p))
            {
            }
        }

        var summary = new TraceSummary { Spans = spans.Count };
        foreach (var (span, step) in steps)
        {
            bool hasUsage = step.InputTokens is not null || step.OutputTokens is not null;
            step.Counted = hasUsage && !ancestorsOfUsage.Contains(span.SpanId);
            if (step.Counted)
            {
                summary.InputTokens = (summary.InputTokens ?? 0) + (step.InputTokens ?? 0);
                summary.OutputTokens = (summary.OutputTokens ?? 0) + (step.OutputTokens ?? 0);
                summary.ModelCalls++;
                if (step.Model is not null && !summary.Models.Contains(step.Model))
                {
                    summary.Models.Add(step.Model);
                }
            }
        }
        // Se guardan los pasos que dicen algo del agente: modelo, herramientas o tokens.
        summary.Steps = steps.Select(x => x.Step)
            .Where(s => s.Operation is not null || s.Tool is not null || s.InputTokens is not null || s.OutputTokens is not null)
            .Take(200)
            .ToList();
        return summary;
    }

    private static string? Text(SpanInfo span, string key) =>
        span.Attributes.TryGetValue(key, out object? v) && v is not null ? Convert.ToString(v, CultureInfo.InvariantCulture) : null;

    private static int? Number(SpanInfo span, string[] keys)
    {
        foreach (string key in keys)
        {
            if (span.Attributes.TryGetValue(key, out object? v) && v is not null
                && double.TryParse(Convert.ToString(v, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out double n))
            {
                return (int)n;
            }
        }
        return null;
    }
}

// Guarda los spans de las trazas que la plataforma inició (una por caso) hasta calificar el caso.
// Los spans de otras trazas se descartan: el receptor no es un colector general.
public sealed class TraceStore
{
    private sealed class Entry
    {
        public readonly List<SpanInfo> Spans = [];
        public DateTimeOffset Expires;
        public DateTimeOffset LastSpanAt;
    }

    private readonly ConcurrentDictionary<string, Entry> _expected = new();

    public static string NewTraceId() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));

    // Encabezado W3C Trace Context para continuar la traza en el agente.
    public static string TraceParent(string traceId) => $"00-{traceId}-{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8))}-01";

    public string Register(string? traceId = null)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        foreach (var (id, entry) in _expected)
        {
            if (entry.Expires < now)
            {
                _expected.TryRemove(id, out _);
            }
        }
        string trace = traceId ?? NewTraceId();
        _expected[trace] = new Entry { Expires = now.AddMinutes(30) };
        return trace;
    }

    public int Accept(IEnumerable<SpanInfo> spans)
    {
        int accepted = 0;
        foreach (SpanInfo span in spans)
        {
            if (_expected.TryGetValue(span.TraceId, out Entry? entry))
            {
                lock (entry)
                {
                    entry.Spans.Add(span);
                    entry.LastSpanAt = DateTimeOffset.UtcNow;
                }
                accepted++;
            }
        }
        return accepted;
    }

    // Espera a que lleguen las trazas del caso: los exportadores mandan por lotes, unos segundos
    // después de la respuesta. Termina cuando llegó algún span con tokens y pasó un momento sin
    // spans nuevos, o al vencer el tiempo máximo.
    public async Task<TraceSummary?> WaitAsync(string traceId, TimeSpan max, CancellationToken ct, TimeSpan? quiet = null)
    {
        if (!_expected.TryGetValue(traceId, out Entry? entry))
        {
            return null;
        }
        TimeSpan calm = quiet ?? TimeSpan.FromMilliseconds(1500);
        DateTimeOffset deadline = DateTimeOffset.UtcNow + max;
        while (DateTimeOffset.UtcNow < deadline)
        {
            bool done;
            lock (entry)
            {
                done = entry.Spans.Any(HasUsage) && DateTimeOffset.UtcNow - entry.LastSpanAt >= calm;
            }
            if (done)
            {
                break;
            }
            await Task.Delay(200, ct);
        }
        _expected.TryRemove(traceId, out _);
        lock (entry)
        {
            return entry.Spans.Count == 0 ? null : TraceSummary.From(entry.Spans.ToList());
        }
    }

    private static bool HasUsage(SpanInfo span) =>
        span.Attributes.Keys.Any(k => k.Contains("usage", StringComparison.Ordinal) || k.Contains("token_count", StringComparison.Ordinal));
}

// Lee pedidos OTLP de trazas (ExportTraceServiceRequest) en protobuf o JSON.
public static class OtlpTraceParser
{
    public static List<SpanInfo> ParseJson(JsonElement root)
    {
        var spans = new List<SpanInfo>();
        foreach (JsonElement rs in Array(root, "resourceSpans"))
        {
            foreach (JsonElement ss in Array(rs, "scopeSpans").Concat(Array(rs, "instrumentationLibrarySpans")))
            {
                foreach (JsonElement s in Array(ss, "spans"))
                {
                    var attributes = new Dictionary<string, object?>();
                    foreach (JsonElement kv in Array(s, "attributes"))
                    {
                        attributes[kv.GetProperty("key").GetString() ?? ""] = kv.TryGetProperty("value", out JsonElement v) ? JsonValue(v) : null;
                    }
                    spans.Add(new SpanInfo(
                        Hex(Str(s, "traceId")),
                        Hex(Str(s, "spanId")),
                        Str(s, "parentSpanId") is { Length: > 0 } p ? Hex(p) : null,
                        Str(s, "name") ?? "",
                        Nanos(s, "startTimeUnixNano"),
                        Nanos(s, "endTimeUnixNano"),
                        attributes));
                }
            }
        }
        return spans;
    }

    // Campos de opentelemetry/proto/trace/v1/trace.proto y common/v1/common.proto.
    public static List<SpanInfo> ParseProtobuf(byte[] data)
    {
        var spans = new List<SpanInfo>();
        var input = new CodedInputStream(data);
        uint tag;
        while ((tag = input.ReadTag()) != 0)
        {
            if (WireFormat.GetTagFieldNumber(tag) == 1)
            {
                ResourceSpans(input.ReadBytes(), spans);
            }
            else
            {
                input.SkipLastField();
            }
        }
        return spans;
    }

    private static void ResourceSpans(ByteString bytes, List<SpanInfo> spans)
    {
        var input = bytes.CreateCodedInput();
        uint tag;
        while ((tag = input.ReadTag()) != 0)
        {
            // 2 = scope_spans; 1000 = instrumentation_library_spans (versiones viejas).
            if (WireFormat.GetTagFieldNumber(tag) is 2 or 1000)
            {
                ScopeSpans(input.ReadBytes(), spans);
            }
            else
            {
                input.SkipLastField();
            }
        }
    }

    private static void ScopeSpans(ByteString bytes, List<SpanInfo> spans)
    {
        var input = bytes.CreateCodedInput();
        uint tag;
        while ((tag = input.ReadTag()) != 0)
        {
            if (WireFormat.GetTagFieldNumber(tag) == 2)
            {
                spans.Add(Span(input.ReadBytes()));
            }
            else
            {
                input.SkipLastField();
            }
        }
    }

    private static SpanInfo Span(ByteString bytes)
    {
        var input = bytes.CreateCodedInput();
        string traceId = "", spanId = "", name = "";
        string? parent = null;
        ulong start = 0, end = 0;
        var attributes = new Dictionary<string, object?>();
        uint tag;
        while ((tag = input.ReadTag()) != 0)
        {
            switch (WireFormat.GetTagFieldNumber(tag))
            {
                case 1: traceId = Convert.ToHexStringLower(input.ReadBytes().ToByteArray()); break;
                case 2: spanId = Convert.ToHexStringLower(input.ReadBytes().ToByteArray()); break;
                case 4:
                    byte[] p = input.ReadBytes().ToByteArray();
                    parent = p.Length == 0 ? null : Convert.ToHexStringLower(p);
                    break;
                case 5: name = input.ReadString(); break;
                case 7: start = input.ReadFixed64(); break;
                case 8: end = input.ReadFixed64(); break;
                case 9:
                    var (key, value) = KeyValue(input.ReadBytes());
                    attributes[key] = value;
                    break;
                default: input.SkipLastField(); break;
            }
        }
        return new SpanInfo(traceId, spanId, parent, name, start, end, attributes);
    }

    private static (string Key, object? Value) KeyValue(ByteString bytes)
    {
        var input = bytes.CreateCodedInput();
        string key = "";
        object? value = null;
        uint tag;
        while ((tag = input.ReadTag()) != 0)
        {
            switch (WireFormat.GetTagFieldNumber(tag))
            {
                case 1: key = input.ReadString(); break;
                case 2: value = AnyValue(input.ReadBytes()); break;
                default: input.SkipLastField(); break;
            }
        }
        return (key, value);
    }

    private static object? AnyValue(ByteString bytes)
    {
        var input = bytes.CreateCodedInput();
        object? value = null;
        uint tag;
        while ((tag = input.ReadTag()) != 0)
        {
            switch (WireFormat.GetTagFieldNumber(tag))
            {
                case 1: value = input.ReadString(); break;
                case 2: value = input.ReadBool(); break;
                case 3: value = input.ReadInt64(); break;
                case 4: value = input.ReadDouble(); break;
                default: input.SkipLastField(); break; // listas, mapas y bytes no hacen falta aquí
            }
        }
        return value;
    }

    private static IEnumerable<JsonElement> Array(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out JsonElement a) && a.ValueKind == JsonValueKind.Array ? a.EnumerateArray() : [];

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static ulong Nanos(JsonElement e, string name) =>
        e.TryGetProperty(name, out JsonElement v) && (v.ValueKind == JsonValueKind.Number ? v.TryGetUInt64(out ulong n) : ulong.TryParse(v.GetString(), out n)) ? n : 0;

    // OTLP/JSON usa ids en hexadecimal; algunos exportadores los mandan en base64.
    private static string Hex(string? id)
    {
        if (string.IsNullOrEmpty(id))
        {
            return "";
        }
        if (id.All(Uri.IsHexDigit))
        {
            return id.ToLowerInvariant();
        }
        try
        {
            return Convert.ToHexStringLower(Convert.FromBase64String(id));
        }
        catch (FormatException)
        {
            return id;
        }
    }

    private static object? JsonValue(JsonElement v)
    {
        if (v.TryGetProperty("stringValue", out JsonElement s)) return s.GetString();
        if (v.TryGetProperty("intValue", out JsonElement i)) return i.ValueKind == JsonValueKind.String ? long.Parse(i.GetString()!, CultureInfo.InvariantCulture) : i.GetInt64();
        if (v.TryGetProperty("doubleValue", out JsonElement d)) return d.ValueKind == JsonValueKind.String ? double.Parse(d.GetString()!, CultureInfo.InvariantCulture) : d.GetDouble();
        if (v.TryGetProperty("boolValue", out JsonElement b)) return b.GetBoolean();
        return null;
    }
}
