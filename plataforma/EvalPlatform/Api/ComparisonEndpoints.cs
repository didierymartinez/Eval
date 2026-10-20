using EvalPlatform.Data;
using EvalPlatform.Domain;
using EvalPlatform.Services;
using Microsoft.EntityFrameworkCore;

namespace EvalPlatform.Api;

// Comparativas: varias corridas de la misma evaluación (una por variante) lado a lado.
public static partial class ApiEndpoints
{
    private static void MapComparisons(RouteGroupBuilder g)
    {
        g.MapGet("/{id:int}", async (int id, EvalDbContext db) =>
        {
            if (await db.Comparisons.FindAsync(id) is not Comparison comparison)
            {
                return Results.NotFound();
            }
            List<Run> runs = await db.Runs.Where(r => r.ComparisonId == id).OrderBy(r => r.Id).ToListAsync();
            return Results.Ok(new { comparison, runs = runs.Select(RunListItem), report = await ReportAsync(runs, db) });
        });

        // Comparar corridas que ya existen (por ejemplo, antes y después de un cambio): ?ids=12,15
        g.MapGet("/corridas", async (string? ids, EvalDbContext db) =>
        {
            List<int> runIds = (ids ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(x => int.TryParse(x, out int n) ? n : 0).Where(n => n > 0).Distinct().ToList();
            if (runIds.Count is < 2 or > 10)
            {
                return Bad("Elige entre 2 y 10 corridas para comparar.");
            }
            List<Run> runs = await db.Runs.Where(r => runIds.Contains(r.Id)).ToListAsync();
            if (runs.Count != runIds.Count)
            {
                return Results.NotFound();
            }
            if (runs.Select(r => r.EvaluationId).Distinct().Count() > 1)
            {
                return Bad("Solo se pueden comparar corridas de la misma evaluación: los casos tienen que ser los mismos.");
            }
            runs = runIds.Select(i => runs.Single(r => r.Id == i)).ToList();
            return Results.Ok(new { comparison = (Comparison?)null, runs = runs.Select(RunListItem), report = await ReportAsync(runs, db) });
        });

        g.MapGet("/{id:int}/excel", async (int id, EvalDbContext db) =>
        {
            if (await db.Comparisons.FindAsync(id) is not Comparison comparison)
            {
                return Results.NotFound();
            }
            List<Run> runs = await db.Runs.Where(r => r.ComparisonId == id).OrderBy(r => r.Id).ToListAsync();
            byte[] file = ComparisonBuilder.ToExcel(comparison.EvaluationName, comparison.DatasetName, comparison.Note, comparison.CreatedAt, await ReportAsync(runs, db));
            return Results.File(file, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", $"comparativa-{id}-{comparison.CreatedAt.ToLocalTime():yyyyMMdd-HHmm}.xlsx");
        });

        g.MapPost("/{id:int}/cancelar", async (int id, EvalDbContext db, RunQueue queue, RemoteTasks tasks) =>
        {
            if (await db.Comparisons.FindAsync(id) is null)
            {
                return Results.NotFound();
            }
            foreach (Run run in await db.Runs.Where(r => r.ComparisonId == id && (r.Status == RunStatus.Queued || r.Status == RunStatus.Running)).ToListAsync())
            {
                await CancelRunAsync(run, db, queue, tasks);
            }
            return Results.Accepted();
        });

        g.MapDelete("/{id:int}", async (int id, EvalDbContext db) =>
        {
            if (await db.Comparisons.FindAsync(id) is not Comparison comparison)
            {
                return Results.NotFound();
            }
            if (await db.Runs.AnyAsync(r => r.ComparisonId == id && (r.Status == RunStatus.Queued || r.Status == RunStatus.Running)))
            {
                return Conflict("La comparativa sigue en curso. Cancélala primero.");
            }
            var runIds = await db.Runs.Where(r => r.ComparisonId == id).Select(r => r.Id).ToListAsync();
            await db.RunResults.Where(r => runIds.Contains(r.RunId)).ExecuteDeleteAsync();
            await db.RunTasks.Where(t => runIds.Contains(t.RunId)).ExecuteDeleteAsync();
            await db.Runs.Where(r => r.ComparisonId == id).ExecuteDeleteAsync();
            db.Comparisons.Remove(comparison);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });
    }

    private static async Task<ComparisonReport> ReportAsync(List<Run> runs, EvalDbContext db)
    {
        var ids = runs.Select(r => r.Id).ToList();
        var results = (await db.RunResults.Where(r => ids.Contains(r.RunId)).ToListAsync())
            .GroupBy(r => r.RunId)
            .ToDictionary(gr => gr.Key, gr => gr.ToList());
        return ComparisonBuilder.Build(runs, results);
    }
}
