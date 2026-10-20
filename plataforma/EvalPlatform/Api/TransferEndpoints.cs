using System.Text.Json;
using System.Text.Json.Serialization;
using EvalPlatform.Data;
using EvalPlatform.Domain;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EvalPlatform.Api;

// Archivo para llevar resultados de una plataforma a otra (por ejemplo, de una red aislada a la central).
public sealed record ExportPackage(
    string Formato,
    int Version,
    DateTimeOffset ExportadoEn,
    string? Origen,
    ExportEvaluation Evaluacion,
    ExportDataset? Dataset,
    ExportComparison? Comparativa,
    List<ExportRun> Corridas);

public sealed record ExportEvaluation(string Nombre, string ColumnaEntrada, List<CheckDefinition> Comparaciones, int Repeticiones);

public sealed record ExportDataset(string Nombre, List<string> Columnas, List<Dictionary<string, string>> Filas);

public sealed record ExportComparison(string? Nota, DateTimeOffset CreadaEn);

public sealed record ExportRun(Run Corrida, List<RunResult> Resultados);

public sealed class PlatformOptions
{
    // Cómo se llama esta plataforma en los archivos exportados (vacío = el nombre de la máquina).
    public string? Nombre { get; set; }
}

public static partial class ApiEndpoints
{
    public const string ExportFormat = "eval-resultados";
    private const long MaxImportBytes = 200L * 1024 * 1024;

    private static readonly JsonSerializerOptions s_export = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
    };

    private static void MapTransfer(RouteGroupBuilder api)
    {
        api.MapGet("/corridas/{id:int}/exportar", async (int id, EvalDbContext db, Microsoft.Extensions.Options.IOptions<PlatformOptions> platform) =>
        {
            if (await db.Runs.FindAsync(id) is not Run run)
            {
                return Results.NotFound();
            }
            ExportPackage package = await PackageAsync([run], null, db, platform.Value);
            return ExportFile(package, $"corrida-{run.Id}-{run.CreatedAt.ToLocalTime():yyyyMMdd-HHmm}.json");
        });

        api.MapGet("/comparativas/{id:int}/exportar", async (int id, EvalDbContext db, Microsoft.Extensions.Options.IOptions<PlatformOptions> platform) =>
        {
            if (await db.Comparisons.FindAsync(id) is not Comparison comparison)
            {
                return Results.NotFound();
            }
            List<Run> runs = await db.Runs.Where(r => r.ComparisonId == id).OrderBy(r => r.Id).ToListAsync();
            if (runs.Count == 0)
            {
                return Bad("La comparativa no tiene corridas.");
            }
            ExportPackage package = await PackageAsync(runs, comparison, db, platform.Value);
            return ExportFile(package, $"comparativa-{id}-{comparison.CreatedAt.ToLocalTime():yyyyMMdd-HHmm}.json");
        });

        // evaluacion (opcional): agrega las corridas a esa evaluación. Sin ella se crean una evaluación y un dataset nuevos.
        api.MapPost("/importar", async (IFormFile archivo, [FromForm] int? evaluacion, EvalDbContext db) =>
        {
            ExportPackage? package;
            try
            {
                await using Stream stream = archivo.OpenReadStream();
                package = await JsonSerializer.DeserializeAsync<ExportPackage>(stream, s_export);
            }
            catch (JsonException ex)
            {
                return Bad($"El archivo no es una exportación válida: {ex.Message}");
            }
            if (package is null || package.Formato != ExportFormat || package.Evaluacion is null || package.Corridas is null)
            {
                return Bad("El archivo no es una exportación de resultados de esta plataforma (botón «Exportar» de una corrida o una comparativa).");
            }
            if (package.Version != 1)
            {
                return Bad($"El archivo es de una versión más nueva ({package.Version}). Actualiza esta plataforma para importarlo.");
            }
            if (package.Corridas.Count is 0 or > 20)
            {
                return Bad("El archivo debe traer entre 1 y 20 corridas.");
            }

            Evaluation target;
            if (evaluacion is int evaluationId)
            {
                if (await db.Evaluations.FindAsync(evaluationId) is not Evaluation existing)
                {
                    return Bad("La evaluación elegida no existe.");
                }
                target = existing;
            }
            else
            {
                ExportDataset source = package.Dataset ?? new ExportDataset(
                    package.Corridas[0].Corrida.DatasetName,
                    package.Corridas.SelectMany(r => r.Resultados).SelectMany(r => r.Row.Keys).Distinct().ToList(),
                    package.Corridas[0].Resultados.Where(r => r.Attempt == 1).OrderBy(r => r.RowIndex).Select(r => r.Row).ToList());
                var dataset = new Dataset
                {
                    Name = $"{source.Nombre} (importado)",
                    SourceFileName = archivo.FileName,
                    Columns = source.Columnas,
                    Rows = source.Filas,
                };
                db.Datasets.Add(dataset);
                await db.SaveChangesAsync();

                string name = package.Evaluacion.Nombre;
                if (await db.Evaluations.AnyAsync(e => e.Name == name))
                {
                    name = $"{name} (importada)";
                }
                // Sin agente: se ve el historial; para volver a ejecutarla hay que elegir uno al editarla.
                target = new Evaluation
                {
                    Name = name,
                    DatasetId = dataset.Id,
                    InputColumn = package.Evaluacion.ColumnaEntrada,
                    Checks = package.Evaluacion.Comparaciones ?? [],
                    Repetitions = Math.Clamp(package.Evaluacion.Repeticiones, 1, 10),
                };
                db.Evaluations.Add(target);
                await db.SaveChangesAsync();
            }

            int? comparisonId = null;
            if (package.Comparativa is not null)
            {
                var comparison = new Comparison
                {
                    EvaluationId = target.Id,
                    EvaluationName = target.Name,
                    DatasetName = package.Dataset?.Nombre ?? package.Corridas[0].Corrida.DatasetName,
                    Note = package.Comparativa.Nota,
                    CreatedAt = package.Comparativa.CreadaEn,
                };
                db.Comparisons.Add(comparison);
                await db.SaveChangesAsync();
                comparisonId = comparison.Id;
            }

            string origin = $"{(string.IsNullOrWhiteSpace(package.Origen) ? "otra plataforma" : package.Origen)} · exportada el {package.ExportadoEn.ToLocalTime():yyyy-MM-dd HH:mm}";
            var runIds = new List<int>();
            foreach (ExportRun item in package.Corridas)
            {
                Run source = item.Corrida;
                var run = new Run
                {
                    EvaluationId = target.Id,
                    EvaluationName = target.Name,
                    AgentName = source.AgentName,
                    DatasetName = source.DatasetName,
                    Variant = source.Variant,
                    Parameters = source.Parameters ?? [],
                    ComparisonId = comparisonId,
                    JudgeName = source.JudgeName,
                    ImportedFrom = origin,
                    Note = source.Note,
                    Checks = source.Checks ?? [],
                    // Una corrida exportada a medias queda como cancelada: no hay quién la termine aquí.
                    Status = source.Status is RunStatus.Queued or RunStatus.Running ? RunStatus.Cancelled : source.Status,
                    Error = source.Error,
                    Total = source.Total,
                    Completed = item.Resultados.Count,
                    Passed = item.Resultados.Count(r => r.Outcome == CaseOutcome.Passed),
                    Failed = item.Resultados.Count(r => r.Outcome == CaseOutcome.Failed),
                    Errors = item.Resultados.Count(r => r.Outcome == CaseOutcome.Error),
                    CreatedAt = source.CreatedAt,
                    StartedAt = source.StartedAt,
                    FinishedAt = source.FinishedAt ?? source.CreatedAt,
                };
                db.Runs.Add(run);
                await db.SaveChangesAsync();
                foreach (RunResult r in item.Resultados)
                {
                    r.Id = 0;
                    r.RunId = run.Id;
                    r.Row ??= [];
                    r.Context ??= [];
                    r.Tools ??= [];
                    r.ToolCalls ??= [];
                    r.Checks ??= [];
                    r.Trace ??= new();
                }
                db.RunResults.AddRange(item.Resultados);
                await db.SaveChangesAsync();
                runIds.Add(run.Id);
            }
            return Results.Ok(new { evaluationId = target.Id, comparisonId, runIds });
        })
        .DisableAntiforgery()
        .WithMetadata(new RequestSizeLimitAttribute(MaxImportBytes), new RequestFormLimitsAttribute { MultipartBodyLengthLimit = MaxImportBytes });
    }

    private static async Task<ExportPackage> PackageAsync(List<Run> runs, Comparison? comparison, EvalDbContext db, PlatformOptions platform)
    {
        Evaluation? evaluation = await db.Evaluations.FindAsync(runs[0].EvaluationId);
        Dataset? dataset = evaluation is null ? null : await db.Datasets.FindAsync(evaluation.DatasetId);
        var ids = runs.Select(r => r.Id).ToList();
        var results = (await db.RunResults.Where(r => ids.Contains(r.RunId)).OrderBy(r => r.RowIndex).ThenBy(r => r.Attempt).ToListAsync())
            .GroupBy(r => r.RunId)
            .ToDictionary(g => g.Key, g => g.ToList());
        return new ExportPackage(
            ExportFormat,
            1,
            DateTimeOffset.UtcNow,
            string.IsNullOrWhiteSpace(platform.Nombre) ? Environment.MachineName : platform.Nombre.Trim(),
            new ExportEvaluation(
                runs[0].EvaluationName,
                evaluation?.InputColumn ?? "input",
                runs[0].Checks,
                evaluation?.Repetitions ?? 1),
            dataset is null ? null : new ExportDataset(dataset.Name, dataset.Columns, dataset.Rows),
            comparison is null ? null : new ExportComparison(comparison.Note, comparison.CreatedAt),
            runs.Select(r => new ExportRun(r, results.GetValueOrDefault(r.Id) ?? [])).ToList());
    }

    private static IResult ExportFile(ExportPackage package, string fileName) =>
        Results.File(JsonSerializer.SerializeToUtf8Bytes(package, s_export), "application/json", fileName);
}
