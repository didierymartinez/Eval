using ClosedXML.Excel;
using EvalPlatform.Domain;

namespace EvalPlatform.Services;

public sealed record CheckSummary(string Label, string Type, bool Blocking, int Passed, int Failed, int Skipped, int Errors, double? AverageScore);

public sealed record CaseChange(int RowIndex, string Input, CaseOutcome Before, CaseOutcome After);

public sealed record RunSummary(
    int Cases,
    int Passed,
    int Failed,
    int Errors,
    double? PassRate,
    long AverageLatencyMs,
    double? AverageTokens,
    double? PreviousAverageTokens,
    double? AverageModelCalls,
    int? CasesPassingAllAttempts,
    int SkippedJudgeChecks,
    IReadOnlyList<CheckSummary> Checks,
    int? PreviousRunId,
    double? PreviousPassRate,
    IReadOnlyList<CaseChange> Changes);

public static class RunReport
{
    public static RunSummary Summarize(Run run, IReadOnlyList<RunResult> results, Run? previous, IReadOnlyList<RunResult>? previousResults)
    {
        int passed = results.Count(r => r.Outcome == CaseOutcome.Passed);
        int failed = results.Count(r => r.Outcome == CaseOutcome.Failed);
        int errors = results.Count(r => r.Outcome == CaseOutcome.Error);
        double? passRate = results.Count == 0 ? null : (double)passed / results.Count;

        var checks = run.Checks.Select((definition, i) =>
        {
            var items = results.Select(r => i < r.Checks.Count ? r.Checks[i] : null).OfType<CheckResult>().ToList();
            var scored = items.Where(c => c.Score is not null && c.Status is CheckStatus.Passed or CheckStatus.Failed).ToList();
            return new CheckSummary(
                items.FirstOrDefault()?.Label ?? CheckTypes.Find(definition.Type)?.Label ?? definition.Type,
                definition.Type,
                definition.Blocking,
                items.Count(c => c.Status == CheckStatus.Passed),
                items.Count(c => c.Status == CheckStatus.Failed),
                items.Count(c => c.Status == CheckStatus.Skipped),
                items.Count(c => c.Status == CheckStatus.Error),
                scored.Count == 0 ? null : scored.Average(c => c.Score!.Value));
        }).ToList();

        // Con repeticiones: casos que pasaron en todos los intentos (pass^k).
        int? allAttempts = results.Any(r => r.Attempt > 1)
            ? results.GroupBy(r => r.RowIndex).Count(g => g.All(r => r.Outcome == CaseOutcome.Passed))
            : null;

        int skippedJudge = results.Sum(r => r.Checks.Count(c => c.Status == CheckStatus.Skipped && (CheckTypes.Find(c.Type)?.UsesJudge ?? false)));

        var changes = new List<CaseChange>();
        double? previousPassRate = null;
        if (previous is not null && previousResults is { Count: > 0 })
        {
            previousPassRate = (double)previousResults.Count(r => r.Outcome == CaseOutcome.Passed) / previousResults.Count;
            var before = previousResults.Where(r => r.Attempt == 1).ToDictionary(r => r.RowIndex);
            foreach (RunResult now in results.Where(r => r.Attempt == 1).OrderBy(r => r.RowIndex))
            {
                if (before.TryGetValue(now.RowIndex, out RunResult? old) && old.Outcome != now.Outcome)
                {
                    changes.Add(new CaseChange(now.RowIndex, now.Input, old.Outcome, now.Outcome));
                }
            }
        }

        return new RunSummary(
            results.Select(r => r.RowIndex).Distinct().Count(),
            passed, failed, errors, passRate,
            results.Count == 0 ? 0 : (long)results.Average(r => r.LatencyMs),
            AverageTokens(results),
            previousResults is null ? null : AverageTokens(previousResults),
            results.Any(r => r.Trace.Spans > 0) ? results.Where(r => r.Trace.Spans > 0).Average(r => r.Trace.ModelCalls) : null,
            allAttempts,
            skippedJudge,
            checks,
            previous?.Id,
            previousPassRate,
            changes);
    }

    private static double? AverageTokens(IReadOnlyList<RunResult> results)
    {
        var withTokens = results.Where(r => r.Tokens is not null).ToList();
        return withTokens.Count == 0 ? null : withTokens.Average(r => r.Tokens!.Value);
    }

    public static byte[] ToExcel(Run run, IReadOnlyList<RunResult> results, RunSummary summary, IReadOnlyList<string> datasetColumns)
    {
        using var workbook = new XLWorkbook();

        IXLWorksheet resumen = workbook.Worksheets.Add("Resumen");
        resumen.Cell(1, 1).Value = "Evaluación";
        resumen.Cell(1, 2).Value = run.EvaluationName;
        resumen.Cell(2, 1).Value = "Agente";
        resumen.Cell(2, 2).Value = run.AgentName;
        resumen.Cell(3, 1).Value = "Dataset";
        resumen.Cell(3, 2).Value = run.DatasetName;
        resumen.Cell(4, 1).Value = "Fecha";
        resumen.Cell(4, 2).Value = run.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
        resumen.Cell(5, 1).Value = "Aprobados";
        resumen.Cell(5, 2).Value = $"{summary.Passed} de {results.Count}";
        resumen.Cell(6, 1).Value = "Fallidos";
        resumen.Cell(6, 2).Value = summary.Failed;
        resumen.Cell(7, 1).Value = "Errores";
        resumen.Cell(7, 2).Value = summary.Errors;
        resumen.Cell(8, 1).Value = "Qué cambió";
        resumen.Cell(8, 2).Value = run.Note ?? "";
        if (summary.AverageTokens is double tokens)
        {
            resumen.Cell(8, 3).Value = "Tokens promedio";
            resumen.Cell(8, 4).Value = Math.Round(tokens);
        }

        int row = 10;
        string[] headers = ["Comparación", "Cuenta para aprobar", "Pasaron", "Fallaron", "Omitidas", "Errores", "Puntaje promedio"];
        for (int i = 0; i < headers.Length; i++)
        {
            resumen.Cell(row, i + 1).Value = headers[i];
        }
        resumen.Row(row).Style.Font.Bold = true;
        foreach (CheckSummary c in summary.Checks)
        {
            row++;
            resumen.Cell(row, 1).Value = c.Label;
            resumen.Cell(row, 2).Value = c.Blocking ? "Sí" : "No (informativa)";
            resumen.Cell(row, 3).Value = c.Passed;
            resumen.Cell(row, 4).Value = c.Failed;
            resumen.Cell(row, 5).Value = c.Skipped;
            resumen.Cell(row, 6).Value = c.Errors;
            if (c.AverageScore is double avg)
            {
                resumen.Cell(row, 7).Value = Math.Round(avg, 2);
            }
        }
        resumen.Column(1).Style.Font.Bold = true;
        resumen.Columns().AdjustToContents(1, 60);

        IXLWorksheet detalle = workbook.Worksheets.Add("Casos");
        var columns = new List<string> { "Caso", "Intento", "Resultado", "Entrada", "Respuesta del agente", "Latencia (ms)", "Tokens", "Error" };
        foreach (CheckSummary c in summary.Checks)
        {
            columns.Add(c.Label);
            columns.Add($"{c.Label} · motivo");
        }
        columns.AddRange(datasetColumns.Select(c => $"[dataset] {c}"));
        for (int i = 0; i < columns.Count; i++)
        {
            detalle.Cell(1, i + 1).Value = columns[i];
        }
        detalle.Row(1).Style.Font.Bold = true;

        int r = 1;
        foreach (RunResult result in results.OrderBy(x => x.RowIndex).ThenBy(x => x.Attempt))
        {
            r++;
            int col = 1;
            detalle.Cell(r, col++).Value = result.RowIndex + 1;
            detalle.Cell(r, col++).Value = result.Attempt;
            detalle.Cell(r, col++).Value = OutcomeText(result.Outcome);
            detalle.Cell(r, col++).Value = result.Input;
            detalle.Cell(r, col++).Value = result.Output ?? "";
            detalle.Cell(r, col++).Value = result.LatencyMs;
            if (result.Tokens is int t)
            {
                detalle.Cell(r, col).Value = t;
            }
            col++;
            detalle.Cell(r, col++).Value = result.Error ?? "";
            for (int i = 0; i < summary.Checks.Count; i++)
            {
                CheckResult? check = i < result.Checks.Count ? result.Checks[i] : null;
                detalle.Cell(r, col++).Value = check is null ? "" : StatusText(check.Status) + (check.Display is null ? "" : $" ({check.Display})");
                detalle.Cell(r, col++).Value = check?.Reason ?? "";
            }
            foreach (string c in datasetColumns)
            {
                detalle.Cell(r, col++).Value = result.Row.TryGetValue(c, out string? v) ? v : "";
            }
        }
        detalle.SheetView.FreezeRows(1);
        detalle.Columns().AdjustToContents(1, 60);

        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }

    public static byte[] DatasetToExcel(Dataset dataset)
    {
        using var workbook = new XLWorkbook();
        IXLWorksheet sheet = workbook.Worksheets.Add("Casos");
        for (int c = 0; c < dataset.Columns.Count; c++)
        {
            sheet.Cell(1, c + 1).Value = dataset.Columns[c];
        }
        sheet.Row(1).Style.Font.Bold = true;
        for (int r = 0; r < dataset.Rows.Count; r++)
        {
            for (int c = 0; c < dataset.Columns.Count; c++)
            {
                sheet.Cell(r + 2, c + 1).Value = dataset.Rows[r].TryGetValue(dataset.Columns[c], out string? v) ? v : "";
            }
        }
        sheet.SheetView.FreezeRows(1);
        sheet.Columns().AdjustToContents(1, 60);
        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }

    public static string OutcomeText(CaseOutcome outcome) => outcome switch
    {
        CaseOutcome.Passed => "Aprobado",
        CaseOutcome.Failed => "Fallido",
        _ => "Error",
    };

    public static string StatusText(CheckStatus status) => status switch
    {
        CheckStatus.Passed => "Pasó",
        CheckStatus.Failed => "Falló",
        CheckStatus.Skipped => "Omitida",
        _ => "Error",
    };
}
