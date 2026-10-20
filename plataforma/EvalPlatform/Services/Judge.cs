using System.ClientModel;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using EvalPlatform.Data;
using EvalPlatform.Domain;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation;
using Microsoft.Extensions.Options;
using OpenAI;

namespace EvalPlatform.Services;

// Modelo que califica en las comparaciones "(IA)" y "(RAG)". Cualquier API compatible con
// OpenAI: OpenAI, Azure OpenAI (/openai/v1/), Anthropic (/v1/ compatible), Ollama, etc.
// Este es el juez del servidor; cada equipo puede tener el suyo (ver Judge y JudgeResolver).
public sealed class JudgeOptions
{
    public string? Endpoint { get; set; }
    public string? ApiKey { get; set; }
    public string? Model { get; set; }

    // Tokens por mes que puede gastar el juez del servidor (null = sin límite).
    public long? LimiteTokensMes { get; set; }

    // false = las evaluaciones no pueden usar el juez del servidor: cada equipo usa el suyo.
    // También deja de usarse como modelo de los agentes "MCP con modelo" sin modelo propio.
    public bool PermitirEnEvaluaciones { get; set; } = true;
}

public interface IJudgeProvider
{
    // null cuando no hay juez configurado.
    ChatConfiguration? Get();
    string? ModelName { get; }
}

// Crea el cliente de un modelo con API compatible con OpenAI.
public interface IJudgeClientFactory
{
    IChatClient Create(string? endpoint, string model, string? apiKey);
}

public sealed class OpenAiJudgeClientFactory : IJudgeClientFactory
{
    public IChatClient Create(string? endpoint, string model, string? apiKey)
    {
        var clientOptions = new OpenAIClientOptions();
        if (!string.IsNullOrWhiteSpace(endpoint))
        {
            clientOptions.Endpoint = new Uri(endpoint.Trim());
        }
        // Algunos servidores locales no piden clave, pero el cliente exige una.
        var credential = new ApiKeyCredential(string.IsNullOrWhiteSpace(apiKey) ? "sin-clave" : apiKey);
        return new OpenAIClient(credential, clientOptions).GetChatClient(model.Trim()).AsIChatClient();
    }
}

public sealed class OpenAiCompatibleJudgeProvider : IJudgeProvider
{
    private readonly ChatConfiguration? _configuration;

    public OpenAiCompatibleJudgeProvider(IOptions<JudgeOptions> options)
    {
        JudgeOptions o = options.Value;
        if (string.IsNullOrWhiteSpace(o.Model) || (string.IsNullOrWhiteSpace(o.ApiKey) && string.IsNullOrWhiteSpace(o.Endpoint)))
        {
            return;
        }
        _configuration = new ChatConfiguration(new OpenAiJudgeClientFactory().Create(o.Endpoint, o.Model, o.ApiKey));
        ModelName = o.Model;
    }

    public string? ModelName { get; }

    public ChatConfiguration? Get() => _configuration;
}

// Cifra las claves de los jueces de equipo. Las llaves de cifrado viven en la carpeta de datos
// (claves/): si se pierden, hay que volver a escribir las claves de los jueces.
public sealed class JudgeSecrets(IDataProtectionProvider provider)
{
    private readonly IDataProtector _protector = provider.CreateProtector("EvalPlatform.Jueces.v1");

    public string Protect(string apiKey) => _protector.Protect(apiKey);

    public bool TryUnprotect(string? protectedKey, out string? apiKey)
    {
        apiKey = null;
        if (string.IsNullOrEmpty(protectedKey))
        {
            return true;
        }
        try
        {
            apiKey = _protector.Unprotect(protectedKey);
            return true;
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    // Para mostrar sin revelarla: "{{env:X}}" tal cual, o los últimos 4 caracteres.
    public string? Hint(string? protectedKey)
    {
        if (!TryUnprotect(protectedKey, out string? key))
        {
            return "(no se puede leer: vuelve a escribirla)";
        }
        if (string.IsNullOrEmpty(key))
        {
            return null;
        }
        return key.TrimStart().StartsWith("{{env:", StringComparison.Ordinal) ? key.Trim() : $"…{key[^Math.Min(4, key.Length)..]}";
    }
}

// Un juez listo para usar, o el motivo por el que no se puede (Client null).
public sealed record ResolvedJudge(string Key, string Name, string? Model, IChatClient? Client, long? MonthlyLimit, string? Problem, bool SkipWhenMissing = false);

// Decide qué juez califica una evaluación: el de su equipo o el del servidor.
public sealed class JudgeResolver(
    IJudgeProvider server,
    IOptions<JudgeOptions> options,
    IDbContextFactory<EvalDbContext> dbFactory,
    JudgeSecrets secrets,
    IJudgeClientFactory clients)
{
    public const string ServerKey = "servidor";

    // Motivo de las comparaciones omitidas por falta de juez (el resumen de la corrida las cuenta por este texto).
    public const string NotConfiguredReason = "No se ejecutó: el juez IA no está configurado (ver README, sección Juez IA).";

    private readonly ConcurrentDictionary<int, (DateTimeOffset UpdatedAt, IChatClient Client)> _cache = new();

    public static string KeyOf(int? judgeId) => judgeId is int id ? $"juez-{id}" : ServerKey;

    public string ServerName => server.ModelName is null ? "Juez del servidor" : $"Juez del servidor ({server.ModelName})";

    public bool ServerAllowed => options.Value.PermitirEnEvaluaciones;

    public async Task<ResolvedJudge> ResolveAsync(int? judgeId, CancellationToken ct)
    {
        if (judgeId is null)
        {
            if (server.Get() is not ChatConfiguration configuration)
            {
                return new(ServerKey, ServerName, null, null, null, NotConfiguredReason, SkipWhenMissing: true);
            }
            if (!ServerAllowed)
            {
                return new(ServerKey, ServerName, server.ModelName, null, null,
                    "Este servidor no permite usar su juez en las evaluaciones: elige el juez de tu equipo al editar la evaluación (menú Jueces IA).");
            }
            return new(ServerKey, ServerName, server.ModelName, configuration.ChatClient, options.Value.LimiteTokensMes, null);
        }

        await using EvalDbContext db = await dbFactory.CreateDbContextAsync(ct);
        if (await db.Judges.FindAsync([judgeId.Value], ct) is not Judge judge)
        {
            return new(KeyOf(judgeId), "Juez borrado", null, null, null, "El juez IA de esta evaluación ya no existe: elige otro al editar la evaluación.");
        }
        return Resolve(judge);
    }

    public ResolvedJudge Resolve(Judge judge)
    {
        if (_cache.TryGetValue(judge.Id, out var cached) && cached.UpdatedAt == judge.UpdatedAt)
        {
            return new(KeyOf(judge.Id), judge.Name, judge.Model, cached.Client, judge.MonthlyTokenLimit, null);
        }
        if (!secrets.TryUnprotect(judge.ProtectedApiKey, out string? apiKey))
        {
            return new(KeyOf(judge.Id), judge.Name, judge.Model, null, judge.MonthlyTokenLimit,
                $"No se pudo leer la clave guardada del juez «{judge.Name}» (¿se perdió la carpeta claves/ junto a la base?). Vuelve a escribirla en Jueces IA.");
        }
        IChatClient client = clients.Create(judge.Endpoint, judge.Model, Templates.RenderText(apiKey ?? "", new Dictionary<string, string>()));
        _cache[judge.Id] = (judge.UpdatedAt, client);
        return new(KeyOf(judge.Id), judge.Name, judge.Model, client, judge.MonthlyTokenLimit, null);
    }

    public void Forget(int judgeId) => _cache.TryRemove(judgeId, out _);
}

// Tokens que gasta cada juez por mes. Se lleva en memoria (una sola instancia) y en la base.
public sealed class JudgeBudget(IDbContextFactory<EvalDbContext> dbFactory)
{
    private readonly ConcurrentDictionary<(string Key, string Month), long> _used = new();
    private readonly SemaphoreSlim _lock = new(1, 1);

    public static string CurrentMonth => DateTimeOffset.UtcNow.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture);

    public async Task<long> UsedAsync(string key, CancellationToken ct)
    {
        string month = CurrentMonth;
        if (_used.TryGetValue((key, month), out long used))
        {
            return used;
        }
        await using EvalDbContext db = await dbFactory.CreateDbContextAsync(ct);
        used = await db.JudgeUsages.Where(u => u.JudgeKey == key && u.Month == month).Select(u => u.Tokens).FirstOrDefaultAsync(ct);
        return _used.GetOrAdd((key, month), used);
    }

    public async Task AddAsync(string key, long tokens, int calls, CancellationToken ct)
    {
        if (tokens <= 0 && calls <= 0)
        {
            return;
        }
        string month = CurrentMonth;
        await _lock.WaitAsync(ct);
        try
        {
            await using EvalDbContext db = await dbFactory.CreateDbContextAsync(ct);
            JudgeUsage? usage = await db.JudgeUsages.SingleOrDefaultAsync(u => u.JudgeKey == key && u.Month == month, ct);
            if (usage is null)
            {
                usage = new JudgeUsage { JudgeKey = key, Month = month };
                db.JudgeUsages.Add(usage);
            }
            usage.Tokens += tokens;
            usage.Calls += calls;
            await db.SaveChangesAsync(ct);
            _used[(key, month)] = usage.Tokens;
        }
        finally
        {
            _lock.Release();
        }
    }
}

// El juez de un caso: cuenta los tokens que gasta y revisa el límite antes de cada llamada.
public sealed class JudgeSession(ResolvedJudge judge, JudgeBudget budget)
{
    private long _tokens;
    private int _calls;

    public ResolvedJudge Judge => judge;
    public long Tokens => Interlocked.Read(ref _tokens);
    public int Calls => _calls;

    public ChatConfiguration? Chat => judge.Client is null ? null : new ChatConfiguration(new UsageTrackingChatClient(judge.Client, tokens =>
    {
        Interlocked.Add(ref _tokens, tokens);
        Interlocked.Increment(ref _calls);
    }));

    // null = se puede usar el juez. El límite puede pasarse un poco por las llamadas que ya estaban en curso.
    public async Task<(CheckStatus Status, string Reason)?> BlockedAsync(CancellationToken ct)
    {
        if (judge.Client is null)
        {
            return (judge.SkipWhenMissing ? CheckStatus.Skipped : CheckStatus.Error, judge.Problem ?? "El juez IA no está disponible.");
        }
        if (judge.MonthlyLimit is long limit)
        {
            long used = await budget.UsedAsync(judge.Key, ct) + Tokens;
            if (used >= limit)
            {
                return (CheckStatus.Error, $"Se alcanzó el límite mensual del juez «{judge.Name}»: {used:N0} de {limit:N0} tokens. Sube el límite en Jueces IA o espera al próximo mes.");
            }
        }
        return null;
    }

    public Task CommitAsync(CancellationToken ct) => budget.AddAsync(judge.Key, Tokens, Calls, ct);
}

// Suma los tokens de cada respuesta. Si el modelo no informa el uso, lo estima (unos 4 caracteres por token).
public sealed class UsageTrackingChatClient(IChatClient inner, Action<long> onUsage) : DelegatingChatClient(inner)
{
    public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        List<ChatMessage> list = messages as List<ChatMessage> ?? messages.ToList();
        ChatResponse response = await base.GetResponseAsync(list, options, cancellationToken);
        long? reported = response.Usage?.TotalTokenCount ?? (response.Usage?.InputTokenCount + response.Usage?.OutputTokenCount);
        onUsage(reported ?? (list.Sum(m => (long)(m.Text?.Length ?? 0)) + response.Text.Length + 3) / 4);
        return response;
    }
}

// Juez con criterio libre (rúbrica) o de cobertura, con el mismo formato de salida que los
// evaluadores de Microsoft.Extensions.AI.Evaluation: <S0>razonamiento</S0><S1>explicación</S1><S2>puntaje</S2>.
public static partial class CustomJudges
{
    // Corrección frente a la respuesta de referencia, con explicación: qué falta o qué contradice.
    public static Task<(double? Score, string Reason)> CorrectnessAsync(ChatConfiguration judge, string question, string answer, string expected, CancellationToken ct) =>
        AskAsync(judge, $$"""
            Eres un evaluador estricto. Compara el SIGNIFICADO de la RESPUESTA con la RESPUESTA ESPERADA.
            No importa la redacción ni el largo; importa que tenga los mismos datos y pasos, y que no contradiga nada.
            5 = equivalente: tiene todos los datos clave y no contradice;
            4 = casi equivalente: falta un detalle menor;
            3 = parcialmente correcta: falta información importante;
            2 = mayormente incorrecta o incompleta;
            1 = incorrecta, contradice lo esperado o no responde.
            Escribe la explicación en español y nombra los datos que faltan o que están mal, si los hay.

            PREGUNTA O ENTRADA:
            {{question}}

            RESPUESTA ESPERADA:
            {{expected}}

            RESPUESTA:
            {{answer}}

            Da tus respuestas entre las etiquetas: <S0>tu razonamiento</S0>, <S1>tu explicación</S1>, <S2>tu puntaje (solo el número)</S2>.
            """, ct);

    public static Task<(double? Score, string Reason)> RubricAsync(ChatConfiguration judge, string question, string answer, string criterion, CancellationToken ct) =>
        AskAsync(judge, $$"""
            Eres un evaluador estricto. Califica de 1 a 5 qué tanto cumple la RESPUESTA el CRITERIO.
            5 = lo cumple por completo; 4 = lo cumple con detalles menores; 3 = a medias; 2 = casi no; 1 = no lo cumple.
            Escribe la explicación en español, en una o dos frases.

            PREGUNTA O ENTRADA:
            {{question}}

            CRITERIO:
            {{criterion}}

            RESPUESTA:
            {{answer}}

            Da tus respuestas entre las etiquetas: <S0>tu razonamiento</S0>, <S1>tu explicación</S1>, <S2>tu puntaje (solo el número)</S2>.
            """, ct);

    public static Task<(double? Score, string Reason)> CoverageAsync(ChatConfiguration judge, string question, string expected, IReadOnlyList<string> context, CancellationToken ct) =>
        AskAsync(judge, $$"""
            Eres un evaluador estricto de sistemas RAG. Decide si los DOCUMENTOS RECUPERADOS contienen la información
            necesaria para dar la RESPUESTA ESPERADA a la pregunta. No juzgues la redacción: solo si los datos están.
            5 = todos los datos clave de la respuesta esperada están en los documentos;
            4 = casi todos, falta un detalle menor;
            3 = la mitad;
            2 = muy pocos;
            1 = ninguno, o los documentos no tienen relación.
            Escribe la explicación en español y nombra los datos que faltan, si faltan.

            PREGUNTA:
            {{question}}

            RESPUESTA ESPERADA:
            {{expected}}

            DOCUMENTOS RECUPERADOS:
            {{string.Join("\n---\n", context)}}

            Da tus respuestas entre las etiquetas: <S0>tu razonamiento</S0>, <S1>tu explicación</S1>, <S2>tu puntaje (solo el número)</S2>.
            """, ct);

    private static async Task<(double? Score, string Reason)> AskAsync(ChatConfiguration judge, string prompt, CancellationToken ct)
    {
        ChatResponse response = await judge.ChatClient.GetResponseAsync(
            [new ChatMessage(ChatRole.User, prompt)],
            new ChatOptions { Temperature = 0 },
            ct);
        string text = response.Text;
        string reason = Tag(text, 1) ?? text.Trim();
        return double.TryParse(Tag(text, 2)?.Trim(), System.Globalization.CultureInfo.InvariantCulture, out double score) && score is >= 1 and <= 5
            ? (score, reason)
            : (null, $"El juez no devolvió un puntaje válido: {Truncate(text, 300)}");
    }

    private static string? Tag(string text, int n)
    {
        Match m = Regex.Match(text, $@"<S{n}>(.*?)</S{n}>", RegexOptions.Singleline);
        return m.Success ? m.Groups[1].Value.Trim() : null;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}

// Límites del servidor para que un servicio compartido no se dispare en consumo.
public sealed class LimitOptions
{
    // Máximo de casos por ejecución (casos × repeticiones × variantes). 0 = sin límite.
    public int CasosPorEjecucion { get; set; }
}
