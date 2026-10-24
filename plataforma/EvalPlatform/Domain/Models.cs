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

    // Juez IA de las comparaciones con juez (null = el juez del servidor).
    public int? JudgeId { get; set; }

    // Configuraciones a comparar con los mismos casos (modelo, prompt, base de conocimiento…).
    // Vacío = solo el agente de la evaluación, tal cual.
    public List<Variant> Variants { get; set; } = [];

    // Cada corrida nueva queda con revisión pendiente: un revisor externo (por ejemplo Claude Code) vuelve a calificar
    // las comparaciones con juez y devuelve sus puntajes por la API de revisión, para contrastarlos con el juez de la plataforma.
    public bool RequiresReview { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

// Una configuración a comparar: el agente de la evaluación con otros parámetros, u otro agente.
// Los parámetros llegan a la configuración del agente como {{nombre}} (URL, encabezados, cuerpo,
// prompt de sistema y modelo de los agentes MCP) y a los ejecutores en "parameters".
public sealed class Variant
{
    public string Name { get; set; } = "";

    // null = el agente de la evaluación.
    public int? AgentId { get; set; }
    public Dictionary<string, string> Parameters { get; set; } = [];

    // Columnas del caso + parámetros de la variante; si se llaman igual, gana el parámetro.
    public static Dictionary<string, string> Merge(IReadOnlyDictionary<string, string> row, IReadOnlyDictionary<string, string>? parameters)
    {
        var merged = new Dictionary<string, string>(row, StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in parameters ?? new Dictionary<string, string>())
        {
            merged[key] = value;
        }
        return merged;
    }
}

// Modelo juez de un equipo: su endpoint, su modelo, su clave y su límite de consumo.
// Así cada equipo paga los tokens de sus evaluaciones y no el dueño del servidor.
public sealed class Judge
{
    public int Id { get; set; }
    public string Name { get; set; } = "";

    // API compatible con OpenAI. Vacío = api.openai.com.
    public string? Endpoint { get; set; }
    public string Model { get; set; } = "";

    // Clave cifrada (Data Protection, llaves en la carpeta de datos). Nunca sale por la API.
    // Puede ser {{env:VARIABLE}} para leerla del servidor.
    public string? ProtectedApiKey { get; set; }

    // Tokens por mes calendario (null = sin límite).
    public long? MonthlyTokenLimit { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

// Tokens que consumió un juez en un mes ("servidor" o el id del juez).
public sealed class JudgeUsage
{
    public long Id { get; set; }
    public string JudgeKey { get; set; } = "";
    public string Month { get; set; } = "";
    public long Tokens { get; set; }
    public int Calls { get; set; }
}

// Varias corridas lanzadas juntas, una por variante, para verlas lado a lado.
public sealed class Comparison
{
    public int Id { get; set; }
    public int EvaluationId { get; set; }
    public string EvaluationName { get; set; } = "";
    public string DatasetName { get; set; } = "";
    public string? Note { get; set; }
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

    // Solo se evalúa si este campo de la respuesta vale OnlyWhenValue (p. ej. puede_responder = true);
    // si no, la comparación se omite. Sirve para no gastar juez en casos donde no aplica.
    public string? OnlyWhenPath { get; set; }
    public string? OnlyWhenValue { get; set; }

    // Indicaciones extra para el juez IA de esta evaluación (correcta, rúbrica, cobertura),
    // p. ej. qué datos de la respuesta esperada no exigir o cuándo bajar el puntaje.
    public string? JudgeInstructions { get; set; }
}

public enum RunStatus { Queued, Running, Completed, Failed, Cancelled }

public sealed class Run
{
    public int Id { get; set; }
    public int EvaluationId { get; set; }
    public string EvaluationName { get; set; } = "";
    public string AgentName { get; set; } = "";
    public string DatasetName { get; set; } = "";

    // Agente que se llamó (null en corridas anteriores a las variantes = el de la evaluación).
    public int? AgentId { get; set; }

    // Variante de la evaluación que se corrió y sus parámetros (null = sin variantes).
    public string? Variant { get; set; }
    public Dictionary<string, string> Parameters { get; set; } = [];

    // Comparativa a la que pertenece, si se lanzó junto con otras variantes.
    public int? ComparisonId { get; set; }

    // Juez IA que calificó (null = el del servidor) y su nombre en ese momento.
    public int? JudgeId { get; set; }
    public string? JudgeName { get; set; }

    // De dónde vino, si se importó de otra plataforma (por ejemplo, de una red aislada).
    public string? ImportedFrom { get; set; }

    // Grupo de ejecutores que atiende esta corrida (null = la plataforma).
    public string? Executor { get; set; }

    // Qué cambió antes de esta corrida (prompt, modelo, temperatura…), para comparar el antes y el después.
    public string? Note { get; set; }

    // Copia de las comparaciones usadas, para que cambiar la evaluación no altere corridas viejas.
    public List<CheckDefinition> Checks { get; set; } = [];

    public RunStatus Status { get; set; } = RunStatus.Queued;
    public string? Error { get; set; }

    // Revisión externa de las comparaciones con juez: null (no aplica), "pendiente" o "revisada".
    public string? ReviewStatus { get; set; }
    public string? Reviewer { get; set; }
    public DateTimeOffset? ReviewedAt { get; set; }

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

    // Tokens que gastó el juez IA en calificar este caso.
    public int? JudgeTokens { get; set; }
    public string? TraceId { get; set; }
    public TraceSummary Trace { get; set; } = new();
    public CaseOutcome Outcome { get; set; }
    public string? Error { get; set; }
    public List<CheckResult> Checks { get; set; } = [];

    // Calificaciones del revisor externo para las mismas comparaciones con juez (mismas etiquetas que Checks).
    public List<CheckResult> ReviewChecks { get; set; } = [];
}

public static class ReviewStatuses
{
    public const string Pending = "pendiente";
    public const string Done = "revisada";
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
