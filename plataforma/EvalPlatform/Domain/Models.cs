using EvalPlatform.Services;

namespace EvalPlatform.Domain;

public static class AgentKinds
{
    // Cualquier cosa que responda por HTTP: webhook, endpoint propio, API de un modelo.
    public const string Http = "http";

    // Servidor MCP: se llama una herramienta (tools/call) con los argumentos de cada caso.
    public const string McpDirect = "mcp";

    // Servidor MCP con un modelo: el modelo recibe las herramientas del MCP y decide cuál usar.
    public const string McpModel = "mcp_modelo";

    // Tu propio código (cualquier lenguaje) ejecuta el agente y reporta las respuestas a la API de ejecutores.
    public const string Code = "codigo";

    public static bool IsMcp(string? kind) => kind is McpDirect or McpModel;
}

// Configuración de los agentes MCP.
public sealed class McpSettings
{
    // Llamada directa: nombre de la herramienta y argumentos (JSON). Admiten {{columna}}.
    public string ToolTemplate { get; set; } = "{{herramienta}}";
    public string ArgumentsTemplate { get; set; } = "{{argumentos}}";

    // Con modelo: API compatible con OpenAI. Sin modelo se usa el mismo del juez IA.
    public string? ModelEndpoint { get; set; }
    public string? ModelName { get; set; }
    public string? ModelApiKey { get; set; }
    public string? SystemPrompt { get; set; }
    public float? Temperature { get; set; }

    // Herramientas que se le ofrecen al modelo, separadas por coma. Vacío = todas.
    public string? AllowedTools { get; set; }
}

// Dónde está el agente y cómo hablarle.
public sealed class Agent
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Kind { get; set; } = AgentKinds.Http;
    public McpSettings Mcp { get; set; } = new();

    // Grupo de ejecutores que llama al agente desde su red. Vacío = lo llama la plataforma.
    public string? Executor { get; set; }

    // Tokens y pasos por las trazas OpenTelemetry que el agente envía a la plataforma (/v1/traces),
    // para servicios que no devuelven los tokens en su respuesta.
    public bool TracesEnabled { get; set; }
    public int TraceWaitSeconds { get; set; } = 15;
    public string Url { get; set; } = "";
    public string Method { get; set; } = "POST";

    // Encabezados; los valores admiten {{columna}} y {{env:VARIABLE}} para no guardar secretos aquí.
    public Dictionary<string, string> Headers { get; set; } = [];

    // JSON con marcadores {{input}} y {{columna}} dentro de los textos.
    public string BodyTemplate { get; set; } = """{ "input": "{{input}}" }""";

    // Rutas dentro de la respuesta JSON (ver JsonPath). Vacía = todo el cuerpo.
    public string ResponsePath { get; set; } = "";
    public string? ContextPath { get; set; }
    public string? ToolsPath { get; set; }

    // Argumentos de cada herramienta, en el mismo orden que ToolsPath (p. ej. "toolCalls[*].arguments").
    public string? ToolArgumentsPath { get; set; }

    // Tokens consumidos; varias rutas separadas por coma se suman (p. ej. "usage.input_tokens, usage.output_tokens").
    public string? TokensPath { get; set; }

    public int TimeoutSeconds { get; set; } = 120;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    // Qué datos puede entregar este agente para las comparaciones.
    // Con "código propio" se asume que tu código reporta lo que necesiten las comparaciones.
    public bool ProvidesTools => Kind == AgentKinds.Code || AgentKinds.IsMcp(Kind) || !string.IsNullOrWhiteSpace(ToolsPath);
    public bool ProvidesToolArguments => Kind == AgentKinds.Code || AgentKinds.IsMcp(Kind) || !string.IsNullOrWhiteSpace(ToolArgumentsPath);
    public bool ProvidesContext => Kind is AgentKinds.Code or AgentKinds.McpModel || (Kind == AgentKinds.Http && !string.IsNullOrWhiteSpace(ContextPath));
    public bool ProvidesTokens => TracesEnabled || Kind is AgentKinds.Code or AgentKinds.McpModel || (Kind == AgentKinds.Http && !string.IsNullOrWhiteSpace(TokensPath));
    public bool RunsRemotely => Kind == AgentKinds.Code || !string.IsNullOrWhiteSpace(Executor);
}

// Una llamada a herramienta: nombre y argumentos en JSON.
public sealed class ToolCallRecord
{
    public string Name { get; set; } = "";
    public string? Arguments { get; set; }
    public string? Result { get; set; }

    public override string ToString() => string.IsNullOrEmpty(Arguments) ? Name : $"{Name}({Arguments})";
}

// Los casos de prueba: una tabla con columnas libres (entrada, esperado, etc.).
public sealed class Dataset
{
    public int Id { get; set; }
    public string Name { get; set; } = "";

    // Archivo subido o enlace (Google Sheets / CSV) desde el que se puede volver a cargar.
    public string? SourceUrl { get; set; }
    public string? SourceFileName { get; set; }

    public List<string> Columns { get; set; } = [];
    public List<Dictionary<string, string>> Rows { get; set; } = [];
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

// Qué agente, con qué casos y qué se compara en cada caso.
public sealed class Evaluation
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public int AgentId { get; set; }
    public int DatasetId { get; set; }

    // Columna del dataset que se manda al agente como {{input}}.
    public string InputColumn { get; set; } = "input";
    public List<CheckDefinition> Checks { get; set; } = [];

    // Veces que se corre cada caso (los LLM no responden siempre igual).
    public int Repetitions { get; set; } = 1;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class CheckDefinition
{
    public string Type { get; set; } = CheckTypes.Contains;
    public string? Label { get; set; }

    // De dónde sale lo esperado: una columna del dataset o un valor fijo.
    public string? ExpectedColumn { get; set; }
    public string? ExpectedValue { get; set; }

    // Qué parte de la respuesta se compara. Vacío = la respuesta principal del agente.
    public string? ResponsePath { get; set; }

    public double? Threshold { get; set; }

    // false = solo informativa: se mide y se muestra, pero no hace fallar el caso.
    public bool Blocking { get; set; } = true;
}

public enum RunStatus { Queued, Running, Completed, Failed, Cancelled }

public sealed class Run
{
    public int Id { get; set; }
    public int EvaluationId { get; set; }
    public string EvaluationName { get; set; } = "";
    public string AgentName { get; set; } = "";
    public string DatasetName { get; set; } = "";

    // Grupo de ejecutores que atiende esta corrida (null = la plataforma).
    public string? Executor { get; set; }

    // Qué cambió antes de esta corrida (prompt, modelo, temperatura…), para comparar el antes y el después.
    public string? Note { get; set; }

    // Copia de las comparaciones usadas, para que cambiar la evaluación no altere corridas viejas.
    public List<CheckDefinition> Checks { get; set; } = [];

    public RunStatus Status { get; set; } = RunStatus.Queued;
    public string? Error { get; set; }
    public int Total { get; set; }
    public int Completed { get; set; }
    public int Passed { get; set; }
    public int Failed { get; set; }
    public int Errors { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
}

public enum CaseOutcome { Passed, Failed, Error }

public sealed class RunResult
{
    public long Id { get; set; }
    public int RunId { get; set; }
    public int RowIndex { get; set; }
    public int Attempt { get; set; } = 1;
    public Dictionary<string, string> Row { get; set; } = [];
    public string Input { get; set; } = "";
    public string? Output { get; set; }
    public List<string> Context { get; set; } = [];
    public List<string> Tools { get; set; } = [];
    public List<ToolCallRecord> ToolCalls { get; set; } = [];
    public string? RawResponse { get; set; }
    public int? HttpStatus { get; set; }
    public long LatencyMs { get; set; }
    public int? Tokens { get; set; }
    public string? TraceId { get; set; }
    public TraceSummary Trace { get; set; } = new();
    public CaseOutcome Outcome { get; set; }
    public string? Error { get; set; }
    public List<CheckResult> Checks { get; set; } = [];
}

public enum CheckStatus { Passed, Failed, Skipped, Error }

public sealed class CheckResult
{
    public string Type { get; set; } = "";
    public string Label { get; set; } = "";
    public CheckStatus Status { get; set; }
    public bool Blocking { get; set; } = true;

    // Puntaje normalizado de 0 a 1 (si aplica) y cómo mostrarlo (p. ej. "4/5", "0.83").
    public double? Score { get; set; }
    public string? Display { get; set; }
    public string? Expected { get; set; }
    public string Reason { get; set; } = "";
}

public enum RunTaskState { Pending, Claimed, Done, Cancelled }

// Un caso de una corrida que ejecuta un ejecutor remoto. Se reparte con un "préstamo":
// si el ejecutor no responde antes de LeaseUntil, el caso vuelve a estar disponible.
public sealed class RunTask
{
    public long Id { get; set; }
    public int RunId { get; set; }
    public string Group { get; set; } = "";
    public int RowIndex { get; set; }
    public int Attempt { get; set; } = 1;
    public string Input { get; set; } = "";
    public Dictionary<string, string> Row { get; set; } = [];
    public RunTaskState State { get; set; } = RunTaskState.Pending;
    public string? Worker { get; set; }
    public int Deliveries { get; set; }
    public DateTimeOffset? LeaseUntil { get; set; }
    public string? TraceId { get; set; }
}
