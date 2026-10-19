namespace AgentEndpoint;

// Contrato del endpoint de evaluación. Es el mismo que consumen promptfoo
// (promptfoo/promptfooconfig.yaml) y el runner .NET (dotnet/AgentEvals.Tests).
// Mantenlo estable: si cambia, cambian los dos arneses.

public sealed record EvalRunRequest(
    string Input,
    string? SessionId = null,
    Dictionary<string, string>? Context = null);

public sealed record EvalRunResponse(
    string Output,
    IReadOnlyList<ToolCallRecord> ToolCalls,
    UsageInfo? Usage,
    long LatencyMs,
    string? TraceId);

public sealed record ToolCallRecord(
    string Name,
    IReadOnlyDictionary<string, object?> Arguments,
    string? Result);

public sealed record UsageInfo(int InputTokens, int OutputTokens);
