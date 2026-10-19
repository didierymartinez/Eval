using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using EvalPlatform.Domain;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation;
using Microsoft.Extensions.AI.Evaluation.Quality;

namespace EvalPlatform.Services;

public sealed record CaseData(Agent Agent, string Input, IReadOnlyDictionary<string, string> Row, AgentInvocation Invocation);

// Aplica una comparación a la respuesta de un caso.
public sealed class CheckRunner(IJudgeProvider judgeProvider)
{
    private static readonly CompareInfo s_compare = CultureInfo.InvariantCulture.CompareInfo;
    private const CompareOptions IgnoreCaseAndAccents = CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace;

    public async Task<CheckResult> RunAsync(CheckDefinition check, CaseData data, CancellationToken cancellationToken)
    {
        CheckTypeInfo? info = CheckTypes.Find(check.Type);
        var result = new CheckResult
        {
            Type = check.Type,
            Label = string.IsNullOrWhiteSpace(check.Label) ? info?.Label ?? check.Type : check.Label,
            Blocking = check.Blocking,
        };
        if (info is null)
        {
            return Set(result, CheckStatus.Error, $"Tipo de comparación desconocido: {check.Type}");
        }

        string? expected = ExpectedValue(check, data);
        result.Expected = expected;
        if (info.NeedsExpected && string.IsNullOrWhiteSpace(expected))
        {
            return Set(result, CheckStatus.Skipped, check.ExpectedColumn is { Length: > 0 } column
                ? $"La columna '{column}' está vacía en este caso."
                : "No hay valor esperado configurado.");
        }

        string actual = string.IsNullOrWhiteSpace(check.ResponsePath)
            ? data.Invocation.Output
            : JsonPath.SelectText(data.Invocation.Json, check.ResponsePath);

        try
        {
            return info.UsesJudge
                ? await RunJudgeAsync(check, info, result, data, actual, expected ?? "", cancellationToken)
                : RunDeterministic(check, info, result, data, actual, expected ?? "");
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return Set(result, CheckStatus.Error, $"Error al evaluar: {ex.Message}");
        }
    }

    private static string? ExpectedValue(CheckDefinition check, CaseData data)
    {
        if (!string.IsNullOrWhiteSpace(check.ExpectedColumn))
        {
            return data.Row.TryGetValue(check.ExpectedColumn, out string? value) ? value : null;
        }
        // El valor fijo también admite {{columna}}, p. ej. un criterio que menciona datos del caso.
        return check.ExpectedValue is null ? null : Templates.RenderText(check.ExpectedValue, AgentInvoker.Variables(data.Input, data.Row));
    }

    private static CheckResult RunDeterministic(CheckDefinition check, CheckTypeInfo info, CheckResult result, CaseData data, string actual, string expected)
    {
        switch (check.Type)
        {
            case CheckTypes.Exact:
            {
                string a = Normalize(actual), e = Normalize(expected);
                bool pass = a == e;
                return Score(result, pass ? 1 : 0, pass, pass ? "Coincide con lo esperado."
                    : $"Se esperaba \"{Readable(e, expected)}\" y llegó \"{Readable(a, Short(actual))}\".");
            }
            case CheckTypes.Contains:
            {
                string[] values = SplitList(expected);
                string[] missing = values.Where(v => s_compare.IndexOf(actual, v, IgnoreCaseAndAccents) < 0).ToArray();
                return Score(result, (double)(values.Length - missing.Length) / values.Length, missing.Length == 0,
                    missing.Length == 0 ? "Contiene todo lo esperado." : $"Falta: {string.Join(", ", missing.Select(m => $"\"{m}\""))}.");
            }
            case CheckTypes.NotContains:
            {
                string[] found = SplitList(expected).Where(v => s_compare.IndexOf(actual, v, IgnoreCaseAndAccents) >= 0).ToArray();
                return Score(result, found.Length == 0 ? 1 : 0, found.Length == 0,
                    found.Length == 0 ? "No contiene ningún texto prohibido." : $"Contiene: {string.Join(", ", found.Select(f => $"\"{f}\""))}.");
            }
            case CheckTypes.Similarity:
            {
                double threshold = check.Threshold ?? info.DefaultThreshold ?? 0.8;
                double similarity = Similarity(Normalize(actual), Normalize(expected));
                result.Display = similarity.ToString("0.00", CultureInfo.InvariantCulture);
                return Score(result, similarity, similarity >= threshold, $"Similitud {result.Display} (mínimo {threshold.ToString("0.00", CultureInfo.InvariantCulture)}).");
            }
            case CheckTypes.Regex:
            {
                Regex regex;
                try
                {
                    regex = new Regex(expected, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
                }
                catch (ArgumentException ex)
                {
                    return Set(result, CheckStatus.Error, $"El patrón no es válido: {ex.Message}");
                }
                bool pass = regex.IsMatch(actual);
                return Score(result, pass ? 1 : 0, pass, pass ? "Cumple el patrón." : $"No cumple el patrón {expected}.");
            }
            case CheckTypes.Tools:
            {
                if (string.IsNullOrWhiteSpace(data.Agent.ToolsPath))
                {
                    return Set(result, CheckStatus.Error, "Configura en el agente la ruta de las herramientas para usar esta comparación.");
                }
                List<string> called = data.Invocation.Tools;
                string calledText = called.Count == 0 ? "ninguna" : string.Join(", ", called);
                string[] wanted = SplitList(expected);
                if (wanted is [var only] && Normalize(only) == "ninguna")
                {
                    return Score(result, called.Count == 0 ? 1 : 0, called.Count == 0, $"Se esperaba no llamar herramientas; llamó: {calledText}.");
                }
                string[] missing = wanted.Where(w => !called.Contains(w, StringComparer.OrdinalIgnoreCase)).ToArray();
                return Score(result, (double)(wanted.Length - missing.Length) / wanted.Length, missing.Length == 0,
                    missing.Length == 0 ? $"Llamó lo esperado ({calledText})." : $"Faltó llamar: {string.Join(", ", missing)}. Llamó: {calledText}.");
            }
            case CheckTypes.Latency:
            {
                double limit = check.Threshold ?? info.DefaultThreshold ?? 15000;
                long ms = data.Invocation.LatencyMs;
                result.Display = $"{ms} ms";
                return Score(result, ms <= limit ? 1 : 0, ms <= limit, $"Respondió en {ms} ms (máximo {limit:0} ms).");
            }
            default:
                return Set(result, CheckStatus.Error, $"Tipo de comparación sin implementar: {check.Type}");
        }
    }

    private async Task<CheckResult> RunJudgeAsync(CheckDefinition check, CheckTypeInfo info, CheckResult result, CaseData data, string actual, string expected, CancellationToken ct)
    {
        ChatConfiguration? judge = judgeProvider.Get();
        if (judge is null)
        {
            return Set(result, CheckStatus.Skipped, "No se ejecutó: el juez IA no está configurado (ver README, sección Juez IA).");
        }

        bool needsContext = info.Family == CheckFamily.Rag;
        if (needsContext && string.IsNullOrWhiteSpace(data.Agent.ContextPath))
        {
            return Set(result, CheckStatus.Error, "Configura en el agente la ruta del contexto (documentos recuperados) para usar esta comparación.");
        }
        if (needsContext && data.Invocation.Context.Count == 0)
        {
            result.Display = "1/5";
            return Score(result, 0, false, "El agente no recuperó ningún documento.");
        }

        IList<ChatMessage> messages = [new ChatMessage(ChatRole.User, data.Input)];
        var response = new ChatResponse(new ChatMessage(ChatRole.Assistant, actual));
        double? score;
        string reason;

        switch (check.Type)
        {
            case CheckTypes.Correctness:
                (score, reason) = await CustomJudges.CorrectnessAsync(judge, data.Input, actual, expected, ct);
                break;
            case CheckTypes.Relevance:
                (score, reason) = Read(await new RelevanceEvaluator().EvaluateAsync(messages, response, judge, cancellationToken: ct));
                break;
            case CheckTypes.Groundedness:
                (score, reason) = Read(await new GroundednessEvaluator().EvaluateAsync(messages, response, judge, [new GroundednessEvaluatorContext(string.Join("\n---\n", data.Invocation.Context))], ct));
                break;
            case CheckTypes.Rubric:
                (score, reason) = await CustomJudges.RubricAsync(judge, data.Input, actual, expected, ct);
                break;
            case CheckTypes.Coverage:
                (score, reason) = await CustomJudges.CoverageAsync(judge, data.Input, expected, data.Invocation.Context, ct);
                break;
            default:
                return Set(result, CheckStatus.Error, $"Tipo de comparación sin implementar: {check.Type}");
        }

        if (score is null)
        {
            return Set(result, CheckStatus.Error, reason);
        }

        double threshold = check.Threshold ?? info.DefaultThreshold ?? 4;
        result.Display = $"{score:0.#}/5";
        return Score(result, score.Value / 5, score.Value >= threshold, reason);
    }

    private static (double? Score, string Reason) Read(EvaluationResult evaluation)
    {
        NumericMetric metric = evaluation.Metrics.Values.OfType<NumericMetric>().First();
        if (metric.Value is null)
        {
            string diagnostics = string.Join(" ", metric.Diagnostics?.Select(d => d.Message) ?? []);
            return (null, $"El juez no devolvió puntaje. {diagnostics}".Trim());
        }
        return (metric.Value, metric.Reason ?? "");
    }

    private static CheckResult Score(CheckResult result, double score, bool pass, string reason)
    {
        result.Score = Math.Clamp(score, 0, 1);
        return Set(result, pass ? CheckStatus.Passed : CheckStatus.Failed, reason);
    }

    private static CheckResult Set(CheckResult result, CheckStatus status, string reason)
    {
        result.Status = status;
        result.Reason = reason;
        return result;
    }

    private static string[] SplitList(string value) =>
        value.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    // Sin mayúsculas, tildes ni espacios repetidos; sí/no y true/false cuentan como lo mismo.
    public static string Normalize(string? value)
    {
        string text = string.Join(' ', (value ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();
        text = new string(text.Normalize(NormalizationForm.FormD).Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark).ToArray());
        return text switch
        {
            "si" or "true" or "1" or "yes" or "verdadero" => "true",
            "no" or "false" or "0" or "falso" => "false",
            _ => text,
        };
    }

    // 1 - distancia de edición / largo mayor (como "String similarity" de n8n).
    public static double Similarity(string a, string b)
    {
        if (a.Length == 0 && b.Length == 0)
        {
            return 1;
        }
        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++)
        {
            previous[j] = j;
        }
        for (int i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
            }
            (previous, current) = (current, previous);
        }
        return 1 - (double)previous[b.Length] / Math.Max(a.Length, b.Length);
    }

    // true/false se muestran como sí/no para que el motivo se entienda sin saber de JSON.
    private static string Readable(string normalized, string original) => normalized switch
    {
        "true" => "sí",
        "false" => "no",
        _ => original,
    };

    private static string Short(string value) => value.Length <= 120 ? value : value[..120] + "…";
}
