using EvalPlatform.Data;
using EvalPlatform.Domain;
using Microsoft.EntityFrameworkCore;

namespace EvalPlatform.Api;

// Puntaje de 1 a 5 que da el revisor externo a una comparación con juez de un caso.
public sealed record ReviewScoreInput(string Label, int Score, string? Reason);

public sealed record ReviewCaseInput(long ResultId, List<ReviewScoreInput> Checks);

public sealed record ReviewInput(string? Reviewer, List<ReviewCaseInput> Results);

// Revisión externa: un revisor (por ejemplo Claude Code, cuando el usuario lo pide o en una tarea programada) toma las
// corridas pendientes, vuelve a calificar las comparaciones con juez con la misma escala y devuelve sus puntajes.
// La plataforma no llama al revisor: solo guarda lo pendiente y lo que le devuelven, y compara con su propio juez.
public static partial class ApiEndpoints
{
    // Escala que usa el juez de la plataforma en cada tipo, para que el revisor califique igual.
    private static readonly Dictionary<string, string> s_reviewScales = new()
    {
        [CheckTypes.Correctness] = "Compara el SIGNIFICADO de la respuesta con la respuesta esperada (no la redacción). 5 = equivalente: tiene todos los datos clave y no contradice; 4 = casi equivalente: falta un detalle menor; 3 = parcialmente correcta: falta información importante; 2 = mayormente incorrecta o incompleta; 1 = incorrecta, contradice lo esperado o no responde.",
        [CheckTypes.Rubric] = "Qué tanto cumple la respuesta el criterio (el valor esperado). 5 = lo cumple por completo; 4 = con detalles menores; 3 = a medias; 2 = casi no; 1 = no lo cumple.",
        [CheckTypes.Coverage] = "Si los documentos recuperados (contexto) contienen la información necesaria para dar la respuesta esperada. 5 = todos los datos clave; 4 = casi todos; 3 = la mitad; 2 = muy pocos; 1 = ninguno o no tienen relación.",
        [CheckTypes.Groundedness] = "Si lo que afirma la respuesta está respaldado por los documentos recuperados (contexto). 5 = todo está respaldado; 4 = casi todo; 3 = la mitad; 2 = poco; 1 = nada o contradice el contexto.",
        [CheckTypes.Relevance] = "Si la respuesta atiende lo que preguntó el usuario. 5 = totalmente relevante; 3 = en parte; 1 = no tiene que ver.",
    };

    private static void MapReviews(RouteGroupBuilder api)
    {
        // Corridas terminadas que esperan revisión.
        api.MapGet("/revisiones/pendientes", async (EvalDbContext db) =>
        {
            var runs = await db.Runs.Where(r => r.ReviewStatus == ReviewStatuses.Pending && r.Status == RunStatus.Completed)
                .OrderBy(r => r.Id).ToListAsync();
            return runs.Select(r => new
            {
                r.Id, r.EvaluationId, r.EvaluationName, r.Variant, r.Note, r.FinishedAt, r.Total,
                Checks = JudgeChecks(r).Select(c => c.Label ?? CheckTypes.Find(c.Type)?.Label ?? c.Type),
            });
        });

        api.MapPost("/corridas/{id:int}/revision/marcar", async (int id, EvalDbContext db) =>
        {
            if (await db.Runs.FindAsync(id) is not Run run)
            {
                return Results.NotFound();
            }
            if (!JudgeChecks(run).Any())
            {
                return Bad("Esta corrida no tiene comparaciones con juez que revisar.");
            }
            run.ReviewStatus = ReviewStatuses.Pending;
            await db.SaveChangesAsync();
            return Results.Ok(new { run.Id, run.ReviewStatus });
        });

        api.MapPost("/corridas/{id:int}/revision/quitar", async (int id, EvalDbContext db) =>
        {
            if (await db.Runs.FindAsync(id) is not Run run)
            {
                return Results.NotFound();
            }
            run.ReviewStatus = run.ReviewedAt is null ? null : ReviewStatuses.Done;
            await db.SaveChangesAsync();
            return Results.Ok(new { run.Id, run.ReviewStatus });
        });

        // Lo que necesita el revisor: las comparaciones con juez (escala e instrucciones) y, por caso, la entrada, la
        // respuesta, lo esperado, el contexto si hace falta y lo que dijo el juez de la plataforma.
        api.MapGet("/corridas/{id:int}/revision", async (int id, EvalDbContext db) =>
        {
            if (await db.Runs.FindAsync(id) is not Run run)
            {
                return Results.NotFound();
            }
            var checks = JudgeChecks(run).Select(c => new
            {
                Label = LabelOf(c),
                c.Type,
                Scale = s_reviewScales.GetValueOrDefault(c.Type, "1 a 5"),
                Instructions = c.JudgeInstructions,
                Threshold = ThresholdOf(c),
                UsesContext = CheckTypes.Find(c.Type)?.Family == CheckFamily.Rag,
            }).ToList();
            var labels = checks.Select(c => c.Label).ToHashSet();
            var results = await db.RunResults.Where(r => r.RunId == id).OrderBy(r => r.RowIndex).ThenBy(r => r.Attempt).ToListAsync();
            var cases = results
                .Select(r => new
                {
                    ResultId = r.Id, r.RowIndex, r.Attempt, r.Input, r.Output,
                    Checks = r.Checks.Where(c => labels.Contains(c.Label) && c.Status is CheckStatus.Passed or CheckStatus.Failed)
                        .Select(c => new { c.Label, c.Expected, PlatformScore = c.Score is double s ? Math.Round(s * 5, 1) : (double?)null, PlatformReason = c.Reason })
                        .ToList(),
                    Context = checks.Any(c => c.UsesContext) ? r.Context : [],
                })
                .Where(c => c.Checks.Count > 0)
                .ToList();
            return Results.Ok(new { Run = RunListItem(run), Checks = checks, Cases = cases });
        });

        api.MapPost("/corridas/{id:int}/revision", async (int id, ReviewInput input, EvalDbContext db) =>
        {
            if (await db.Runs.FindAsync(id) is not Run run)
            {
                return Results.NotFound();
            }
            var definitions = JudgeChecks(run).ToDictionary(LabelOf);
            var results = await db.RunResults.Where(r => r.RunId == id).ToDictionaryAsync(r => r.Id);
            foreach (ReviewCaseInput c in input.Results ?? [])
            {
                if (!results.TryGetValue(c.ResultId, out RunResult? result))
                {
                    return Bad($"El resultado {c.ResultId} no es de esta corrida.");
                }
                foreach (ReviewScoreInput s in c.Checks ?? [])
                {
                    if (!definitions.TryGetValue(s.Label, out CheckDefinition? def))
                    {
                        return Bad($"'{s.Label}' no es una comparación con juez de esta corrida.");
                    }
                    if (s.Score is < 1 or > 5)
                    {
                        return Bad($"El puntaje de '{s.Label}' en el resultado {c.ResultId} debe estar entre 1 y 5.");
                    }
                    var review = new CheckResult
                    {
                        Type = def.Type,
                        Label = s.Label,
                        Blocking = def.Blocking,
                        Score = s.Score / 5.0,
                        Display = $"{s.Score}/5",
                        Expected = result.Checks.FirstOrDefault(x => x.Label == s.Label)?.Expected,
                        Reason = s.Reason?.Trim() ?? "",
                        Status = s.Score >= ThresholdOf(def) ? CheckStatus.Passed : CheckStatus.Failed,
                    };
                    // Se reemplaza la lista para que EF detecte el cambio en la columna JSON.
                    result.ReviewChecks = [.. result.ReviewChecks.Where(x => x.Label != s.Label), review];
                }
            }
            run.ReviewStatus = ReviewStatuses.Done;
            run.Reviewer = string.IsNullOrWhiteSpace(input.Reviewer) ? "Revisor externo" : input.Reviewer.Trim();
            run.ReviewedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
            return Results.Ok(new { run.Id, run.ReviewStatus, Summary = ReviewSummary(results.Values) });
        });

        api.MapGet("/corridas/{id:int}/revision/resumen", async (int id, EvalDbContext db) =>
        {
            if (await db.Runs.FindAsync(id) is not Run run)
            {
                return Results.NotFound();
            }
            var results = await db.RunResults.Where(r => r.RunId == id).ToListAsync();
            return Results.Ok(new { run.ReviewStatus, run.Reviewer, run.ReviewedAt, Summary = ReviewSummary(results) });
        });
    }

    private static IEnumerable<CheckDefinition> JudgeChecks(Run run) =>
        run.Checks.Where(c => CheckTypes.Find(c.Type)?.UsesJudge == true);

    private static string LabelOf(CheckDefinition c) =>
        string.IsNullOrWhiteSpace(c.Label) ? CheckTypes.Find(c.Type)?.Label ?? c.Type : c.Label;

    private static double ThresholdOf(CheckDefinition c) => c.Threshold ?? CheckTypes.Find(c.Type)?.DefaultThreshold ?? 4;

    // Por comparación: cuántos casos calificaron ambos, cuántos aprueba cada uno, en cuántos coinciden y los puntajes medios.
    internal static IEnumerable<object> ReviewSummary(IEnumerable<RunResult> results)
    {
        var pares = results
            .SelectMany(r => r.ReviewChecks.Select(rv => (Review: rv, Platform: r.Checks.FirstOrDefault(p => p.Label == rv.Label))))
            .Where(p => p.Platform is { Status: CheckStatus.Passed or CheckStatus.Failed })
            .GroupBy(p => p.Review.Label);
        return pares.Select(g =>
        {
            var l = g.ToList();
            return (object)new
            {
                Label = g.Key,
                Cases = l.Count,
                PlatformPassed = l.Count(p => p.Platform!.Status == CheckStatus.Passed),
                ReviewPassed = l.Count(p => p.Review.Status == CheckStatus.Passed),
                Agreement = Math.Round((double)l.Count(p => p.Platform!.Status == p.Review.Status) / l.Count, 3),
                PlatformAverage = Math.Round(l.Average(p => (p.Platform!.Score ?? 0) * 5), 2),
                ReviewAverage = Math.Round(l.Average(p => (p.Review.Score ?? 0) * 5), 2),
            };
        }).ToList();
    }
}
