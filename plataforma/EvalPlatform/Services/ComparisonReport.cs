using ClosedXML.Excel;
using EvalPlatform.Domain;

namespace EvalPlatform.Services;

// Una variante dentro de la comparativa: su corrida y sus totales.
public sealed record VariantSummary(
    int RunId,
    string Name,
    string AgentName,
    Dictionary<string, string> Parameters,
    RunStatus Status,
    int Total,
    int Completed,
    int Passed,
    int Failed,
    int Errors,
    double? PassRate,
    long AverageLatencyMs,
    double? AverageTokens,
    long? TotalTokens,
    double? AverageModelCalls,
    int? CasesPassingAllAttempts,
    IReadOnlyList<double?> CheckPassRates,
    IReadOnlyList<double?> CheckAverageScores);

// Cómo le fue a una variante en un caso (con repeticiones: cuántos intentos pasaron).
public sealed record ComparedCell(int RunId, CaseOutcome? Outcome, int Passed, int Attempts, string? Output, string? Error, double? Tokens, long? LatencyMs);

public sealed record ComparedCase(int RowIndex, string Input, bool Differs, IReadOnlyList<ComparedCell> Cells);

public sealed record ComparisonReport(
    IReadOnlyList<string> CheckLabels,
    IReadOnlyList<VariantSummary> Variants,
    IReadOnlyList<ComparedCase> Cases,
    bool Finished,
    int? BestRunId,
    string? Verdict);

// Pone lado a lado las corridas de una misma evaluación (una por variante), caso por caso.
public static class ComparisonBuilder
{
    public static ComparisonReport Build(IReadOnlyList<Run> runs, IReadOnlyDictionary<int, List<RunResult>> results)
    {
        IReadOnlyList<CheckDefinition> checks = runs.FirstOrDefault()?.Checks ?? [];
        List<string> labels = checks.Select((c, i) =>
            runs.SelectMany(r => results.GetValueOrDefault(r.Id) ?? []).Select(x => i < x.Checks.Count ? x.Checks[i].Label : null).FirstOrDefault(l => l is not null)
            ?? c.Label ?? CheckTypes.Find(c.Type)?.Label ?? c.Type).ToList();

        var variants = runs.Select(run => Summarize(run, results.GetValueOrDefault(run.Id) ?? [], checks.Count)).ToList();

        var rows = runs.SelectMany(r => results.GetValueOrDefault(r.Id) ?? [])
            .GroupBy(r => r.RowIndex)
            .OrderBy(g => g.Key)
            .Select(g =>
            {
                var cells = runs.Select(run =>
                {
                    var attempts = g.Where(r => r.RunId == run.Id).OrderBy(r => r.Attempt).ToList();
                    if (attempts.Count == 0)
                    {
                        return new ComparedCell(run.Id, null, 0, 0, null, null, null, null);
                    }
                    var withTokens = attempts.Where(r => r.Tokens is not null).ToList();
                    return new ComparedCell(
                        run.Id,
                        // Aprobado solo si pasó en todos los intentos.
                        attempts.All(r => r.Outcome == CaseOutcome.Passed) ? CaseOutcome.Passed
                            : attempts.All(r => r.Outcome == CaseOutcome.Error) ? CaseOutcome.Error
                            : CaseOutcome.Failed,
                        attempts.Count(r => r.Outcome == CaseOutcome.Passed),
                        attempts.Count,
                        attempts[0].Output,
                        attempts[0].Error,
                        withTokens.Count == 0 ? null : withTokens.Average(r => r.Tokens!.Value),
                        (long)attempts.Average(r => r.LatencyMs));
                }).ToList();
                var outcomes = cells.Where(c => c.Outcome is not null).Select(c => (c.Outcome, c.Passed)).Distinct().Count();
                return new ComparedCase(g.Key, g.First().Input, outcomes > 1, cells);
            })
            .ToList();

        bool finished = runs.All(r => r.Status is RunStatus.Completed or RunStatus.Cancelled or RunStatus.Failed);
        (int? best, string? verdict) = finished ? Verdict(variants) : (null, null);
        return new ComparisonReport(labels, variants, rows, finished, best, verdict);
    }

    private static VariantSummary Summarize(Run run, List<RunResult> results, int checkCount)
    {
        var withTokens = results.Where(r => r.Tokens is not null).ToList();
        var traced = results.Where(r => r.Trace.Spans > 0).ToList();
        var passRates = new List<double?>();
        var scores = new List<double?>();
        for (int i = 0; i < checkCount; i++)
        {
            var graded = results.Select(r => i < r.Checks.Count ? r.Checks[i] : null)
                .OfType<CheckResult>()
                .Where(c => c.Status is CheckStatus.Passed or CheckStatus.Failed)
                .ToList();
            passRates.Add(graded.Count == 0 ? null : (double)graded.Count(c => c.Status == CheckStatus.Passed) / graded.Count);
            var scored = graded.Where(c => c.Score is not null).ToList();
            scores.Add(scored.Count == 0 ? null : scored.Average(c => c.Score!.Value));
        }

        return new VariantSummary(
            run.Id,
            run.Variant ?? $"Corrida #{run.Id}",
            run.AgentName,
            run.Parameters,
            run.Status,
            run.Total,
            results.Count,
            results.Count(r => r.Outcome == CaseOutcome.Passed),
            results.Count(r => r.Outcome == CaseOutcome.Failed),
            results.Count(r => r.Outcome == CaseOutcome.Error),
            results.Count == 0 ? null : (double)results.Count(r => r.Outcome == CaseOutcome.Passed) / results.Count,
            results.Count == 0 ? 0 : (long)results.Average(r => r.LatencyMs),
            withTokens.Count == 0 ? null : withTokens.Average(r => r.Tokens!.Value),
            withTokens.Count == 0 ? null : withTokens.Sum(r => (long)r.Tokens!.Value),
            traced.Count == 0 ? null : traced.Average(r => r.Trace.ModelCalls),
            results.Any(r => r.Attempt > 1) ? results.GroupBy(r => r.RowIndex).Count(g => g.All(r => r.Outcome == CaseOutcome.Passed)) : null,
            passRates,
            scores);
    }

    // La mejor: la que más casos aprueba; si empatan, la que gasta menos tokens y después la más rápida.
    private static (int?, string?) Verdict(List<VariantSummary> variants)
    {
        var ranked = variants.Where(v => v.PassRate is not null)
            .OrderByDescending(v => Math.Round(v.PassRate!.Value, 4))
            .ThenBy(v => v.AverageTokens ?? double.MaxValue)
            .ThenBy(v => v.AverageLatencyMs)
            .ToList();
        if (ranked.Count < 2)
        {
            return (null, null);
        }

        VariantSummary best = ranked[0];
        var tied = ranked.Skip(1).Where(v => Math.Round(v.PassRate!.Value, 4) == Math.Round(best.PassRate!.Value, 4)).ToList();
        string text;
        if (tied.Count == 0)
        {
            VariantSummary second = ranked[1];
            text = $"La que más casos aprueba es «{best.Name}»: {Pct(best.PassRate)}, frente a {Pct(second.PassRate)} de «{second.Name}».";
            if (best.AverageTokens is double bt && second.AverageTokens is double st && st > 0)
            {
                text += bt <= st
                    ? $" Además gasta {Pct((st - bt) / st)} menos tokens."
                    : $" Gasta {Pct((bt - st) / st)} más tokens.";
            }
        }
        else
        {
            string names = string.Join(", ", tied.Select(v => $"«{v.Name}»"));
            text = $"«{best.Name}» y {names} aprueban lo mismo ({Pct(best.PassRate)}).";
            VariantSummary other = tied[0];
            if (best.AverageTokens is double bt && other.AverageTokens is double ot && ot > bt && ot > 0)
            {
                text += $" «{best.Name}» gasta {Pct((ot - bt) / ot)} menos tokens.";
            }
            else if (other.AverageLatencyMs > best.AverageLatencyMs)
            {
                text += $" «{best.Name}» responde más rápido ({best.AverageLatencyMs / 1000.0:0.0} s frente a {other.AverageLatencyMs / 1000.0:0.0} s).";
            }
        }
        if (variants.Any(v => v.Status != RunStatus.Completed))
        {
            text += " Ojo: hay variantes que no terminaron todos sus casos.";
        }
        return (best.RunId, text);
    }

    private static string Pct(double? x) => x is double v ? $"{Math.Round(v * 100)}%" : "—";

    public static byte[] ToExcel(string evaluationName, string datasetName, string? note, DateTimeOffset createdAt, ComparisonReport report)
    {
        using var workbook = new XLWorkbook();

        IXLWorksheet resumen = workbook.Worksheets.Add("Resumen");
        resumen.Cell(1, 1).Value = "Evaluación";
        resumen.Cell(1, 2).Value = evaluationName;
        resumen.Cell(2, 1).Value = "Dataset";
        resumen.Cell(2, 2).Value = datasetName;
        resumen.Cell(3, 1).Value = "Fecha";
        resumen.Cell(3, 2).Value = createdAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
        resumen.Cell(4, 1).Value = "Qué se compara";
        resumen.Cell(4, 2).Value = note ?? "";
        resumen.Cell(5, 1).Value = "Conclusión";
        resumen.Cell(5, 2).Value = report.Verdict ?? "";
        resumen.Range(1, 1, 5, 1).Style.Font.Bold = true;

        int row = 7;
        var headers = new List<string> { "Variante", "Agente", "Parámetros", "Aprobados", "% aprobados", "Fallidos", "Errores", "Tiempo prom. (s)", "Tokens prom.", "Tokens total" };
        headers.AddRange(report.CheckLabels.Select(l => $"{l} (% pasó)"));
        for (int i = 0; i < headers.Count; i++)
        {
            resumen.Cell(row, i + 1).Value = headers[i];
        }
        resumen.Row(row).Style.Font.Bold = true;
        foreach (VariantSummary v in report.Variants)
        {
            row++;
            int col = 1;
            resumen.Cell(row, col++).Value = v.Name + (v.RunId == report.BestRunId ? " ★" : "");
            resumen.Cell(row, col++).Value = v.AgentName;
            resumen.Cell(row, col++).Value = string.Join(", ", v.Parameters.Select(p => $"{p.Key}={p.Value}"));
            resumen.Cell(row, col++).Value = $"{v.Passed} de {v.Completed}";
            if (v.PassRate is double rate)
            {
                resumen.Cell(row, col).Value = rate;
                resumen.Cell(row, col).Style.NumberFormat.Format = "0%";
            }
            col++;
            resumen.Cell(row, col++).Value = v.Failed;
            resumen.Cell(row, col++).Value = v.Errors;
            resumen.Cell(row, col++).Value = Math.Round(v.AverageLatencyMs / 1000.0, 1);
            if (v.AverageTokens is double tokens)
            {
                resumen.Cell(row, col).Value = Math.Round(tokens);
            }
            col++;
            if (v.TotalTokens is long total)
            {
                resumen.Cell(row, col).Value = total;
            }
            col++;
            foreach (double? checkRate in v.CheckPassRates)
            {
                if (checkRate is double cr)
                {
                    resumen.Cell(row, col).Value = cr;
                    resumen.Cell(row, col).Style.NumberFormat.Format = "0%";
                }
                col++;
            }
        }
        resumen.Columns().AdjustToContents(1, 60);

        IXLWorksheet casos = workbook.Worksheets.Add("Casos");
        var columns = new List<string> { "Caso", "Entrada", "Cambia entre variantes" };
        foreach (VariantSummary v in report.Variants)
        {
            columns.Add($"{v.Name} · resultado");
            columns.Add($"{v.Name} · respuesta");
            columns.Add($"{v.Name} · tokens");
        }
        for (int i = 0; i < columns.Count; i++)
        {
            casos.Cell(1, i + 1).Value = columns[i];
        }
        casos.Row(1).Style.Font.Bold = true;
        int r = 1;
        foreach (ComparedCase c in report.Cases)
        {
            r++;
            int col = 1;
            casos.Cell(r, col++).Value = c.RowIndex + 1;
            casos.Cell(r, col++).Value = c.Input;
            casos.Cell(r, col++).Value = c.Differs ? "Sí" : "";
            foreach (ComparedCell cell in c.Cells)
            {
                casos.Cell(r, col++).Value = cell.Outcome is CaseOutcome o
                    ? RunReport.OutcomeText(o) + (cell.Attempts > 1 ? $" ({cell.Passed}/{cell.Attempts})" : "")
                    : "";
                casos.Cell(r, col++).Value = cell.Output ?? cell.Error ?? "";
                if (cell.Tokens is double t)
                {
                    casos.Cell(r, col).Value = Math.Round(t);
                }
                col++;
            }
        }
        casos.SheetView.FreezeRows(1);
        casos.Columns().AdjustToContents(1, 60);

        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }
}
