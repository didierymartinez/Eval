using EvalPlatform.Data;
using EvalPlatform.Domain;
using EvalPlatform.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EvalPlatform.Api;

public sealed record AgentInput(
    string Name,
    string Url,
    string? Method,
    Dictionary<string, string>? Headers,
    string? BodyTemplate,
    string? ResponsePath,
    string? ContextPath,
    string? ToolsPath,
    int? TimeoutSeconds,
    string? TokensPath = null,
    string? Kind = null,
    McpSettings? Mcp = null,
    string? ToolArgumentsPath = null,
    string? Executor = null,
    bool? TracesEnabled = null,
    int? TraceWaitSeconds = null);

public sealed record AgentTestInput(AgentInput Agent, string Input, Dictionary<string, string>? Row);

public sealed record DatasetLinkInput(string Name, string Url);

public sealed record RenameInput(string Name);

// Variant: nombre de una variante de la evaluación (vacío = el agente de la evaluación, sin parámetros).
public sealed record RunInput(string? Note, string? Variant = null);

// Variants: nombres de las variantes a comparar (vacío = todas).
public sealed record CompareInput(string? Note, List<string>? Variants);

public sealed record EvaluationInput(string Name, int AgentId, int DatasetId, string InputColumn, List<CheckDefinition> Checks, int? Repetitions, List<Variant>? Variants = null);

public sealed record AgentTemplate(string Id, string Name, string Description, AgentInput Agent);

public static partial class ApiEndpoints
{
    public static void MapEvalApi(this WebApplication app)
    {
        RouteGroupBuilder api = app.MapGroup("/api");

        api.MapGet("/estado", (IJudgeProvider judge) => new { juezConfigurado = judge.Get() is not null, modeloJuez = judge.ModelName });
        api.MapGet("/comparaciones", () => CheckTypes.All);
        api.MapGet("/plantillas-agente", () => s_templates);

        MapAgents(api.MapGroup("/agentes"));
        MapDatasets(api.MapGroup("/datasets"));
        MapEvaluations(api.MapGroup("/evaluaciones"));
        MapRuns(api.MapGroup("/corridas"));
        MapComparisons(api.MapGroup("/comparativas"));
    }

    private static IResult Bad(string error) => Results.BadRequest(new { error });

    private static IResult Conflict(string error) => Results.Conflict(new { error });

    // ---------- Agentes ----------

    private static void MapAgents(RouteGroupBuilder g)
    {
        g.MapGet("/", async (EvalDbContext db) => await db.Agents.OrderBy(a => a.Name).ToListAsync());

        g.MapGet("/{id:int}", async (int id, EvalDbContext db) =>
            await db.Agents.FindAsync(id) is Agent a ? Results.Ok(a) : Results.NotFound());

        g.MapPost("/", async (AgentInput input, EvalDbContext db) =>
        {
            if (Validate(input) is string error)
            {
                return Bad(error);
            }
            var agent = Apply(new Agent(), input);
            db.Agents.Add(agent);
            await db.SaveChangesAsync();
            return Results.Created($"/api/agentes/{agent.Id}", agent);
        });

        g.MapPut("/{id:int}", async (int id, AgentInput input, EvalDbContext db) =>
        {
            if (await db.Agents.FindAsync(id) is not Agent agent)
            {
                return Results.NotFound();
            }
            if (Validate(input) is string error)
            {
                return Bad(error);
            }
            Apply(agent, input);
            await db.SaveChangesAsync();
            return Results.Ok(agent);
        });

        g.MapDelete("/{id:int}", async (int id, EvalDbContext db) =>
        {
            if (await db.Agents.FindAsync(id) is not Agent agent)
            {
                return Results.NotFound();
            }
            if (await db.Evaluations.AnyAsync(e => e.AgentId == id)
                || (await db.Evaluations.ToListAsync()).Any(e => e.Variants.Any(v => v.AgentId == id)))
            {
                return Conflict("Hay evaluaciones que usan este agente (directamente o en una variante). Bórralas o cámbiales el agente primero.");
            }
            db.Agents.Remove(agent);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        // Herramientas que expone un servidor MCP (sin guardar el agente).
        g.MapPost("/herramientas", async (AgentInput input, McpInvoker mcp, CancellationToken ct) =>
        {
            if (Validate(input) is string error)
            {
                return Bad(error);
            }
            try
            {
                return Results.Ok(await mcp.ListToolsAsync(Apply(new Agent(), input), ct));
            }
            catch (McpConnectionException ex)
            {
                return Bad(ex.Message);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return Bad($"El servidor MCP no respondió en {input.TimeoutSeconds ?? 120} segundos.");
            }
        });

        // Prueba la conexión con un texto de ejemplo, sin guardar nada.
        g.MapPost("/probar", async (AgentTestInput test, AgentDispatcher dispatcher, TraceStore traces, CancellationToken ct) =>
        {
            if (Validate(test.Agent) is string error)
            {
                return Bad(error);
            }
            Agent agent = Apply(new Agent(), test.Agent);
            string? traceId = agent.TracesEnabled ? traces.Register() : null;
            AgentInvocation r = await dispatcher.InvokeAsync(agent, test.Input ?? "", test.Row ?? [], ct, traceId);
            await TraceEnricher.ApplyAsync(traces, agent, r, ct);
            return Results.Ok(new
            {
                ok = r.Succeeded,
                error = r.Error,
                httpStatus = r.HttpStatus,
                latencyMs = r.LatencyMs,
                requestBody = r.RequestBody,
                output = r.Output,
                context = r.Context,
                tools = r.Tools,
                toolCalls = r.ToolCalls,
                tokens = r.Tokens,
                traceId = r.TraceId,
                trace = r.Trace,
                raw = r.RawResponse,
            });
        });
    }

    private static string? Validate(AgentInput input)
    {
        string kind = input.Kind ?? AgentKinds.Http;
        if (kind is not (AgentKinds.Http or AgentKinds.McpDirect or AgentKinds.McpModel or AgentKinds.Code))
        {
            return $"Tipo de agente desconocido: {kind}.";
        }
        string? group = string.IsNullOrWhiteSpace(input.Executor) ? null : input.Executor.Trim().ToLowerInvariant();
        if (kind == AgentKinds.Code && group is null)
        {
            return "Indica el grupo de ejecutores: es el nombre con que tu código pide los casos (ej. red-finanzas).";
        }
        if (group is not null && !RemoteTasks.IsValidGroup(group))
        {
            return "El grupo de ejecutores debe tener letras minúsculas, números, - o _ (ej. red-finanzas).";
        }
        if (string.IsNullOrWhiteSpace(input.Name))
        {
            return "Ponle un nombre al agente.";
        }
        if (kind == AgentKinds.Code)
        {
            return null;
        }
        if (kind == AgentKinds.McpDirect && string.IsNullOrWhiteSpace(input.Mcp?.ToolTemplate))
        {
            return "Indica qué herramienta del MCP se llama (un nombre fijo o {{columna}}).";
        }
        if (string.IsNullOrWhiteSpace(input.Name))
        {
            return "Ponle un nombre al agente.";
        }
        if (string.IsNullOrWhiteSpace(input.Url) || !input.Url.TrimStart().StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            return "La URL del agente debe empezar con http:// o https://.";
        }
        if (input.Method is { Length: > 0 } m && m.Trim().ToUpperInvariant() is not ("GET" or "POST" or "PUT" or "PATCH"))
        {
            return "El método debe ser GET, POST, PUT o PATCH.";
        }
        if (kind == AgentKinds.Http && !string.IsNullOrWhiteSpace(input.BodyTemplate))
        {
            try
            {
                System.Text.Json.Nodes.JsonNode.Parse(input.BodyTemplate);
            }
            catch (System.Text.Json.JsonException ex)
            {
                return $"El cuerpo no es JSON válido: {ex.Message} Los marcadores van entre comillas, por ejemplo \"{{{{input}}}}\".";
            }
        }
        return null;
    }

    private static Agent Apply(Agent agent, AgentInput input)
    {
        agent.Name = input.Name.Trim();
        agent.Url = input.Url?.Trim() ?? "";
        agent.Method = string.IsNullOrWhiteSpace(input.Method) ? "POST" : input.Method.Trim().ToUpperInvariant();
        agent.Headers = (input.Headers ?? []).Where(h => !string.IsNullOrWhiteSpace(h.Key)).ToDictionary(h => h.Key.Trim(), h => h.Value ?? "");
        agent.BodyTemplate = input.BodyTemplate ?? "";
        agent.ResponsePath = input.ResponsePath?.Trim() ?? "";
        agent.ContextPath = string.IsNullOrWhiteSpace(input.ContextPath) ? null : input.ContextPath.Trim();
        agent.ToolsPath = string.IsNullOrWhiteSpace(input.ToolsPath) ? null : input.ToolsPath.Trim();
        agent.TokensPath = string.IsNullOrWhiteSpace(input.TokensPath) ? null : input.TokensPath.Trim();
        agent.ToolArgumentsPath = string.IsNullOrWhiteSpace(input.ToolArgumentsPath) ? null : input.ToolArgumentsPath.Trim();
        agent.Kind = input.Kind ?? AgentKinds.Http;
        agent.Executor = string.IsNullOrWhiteSpace(input.Executor) ? null : input.Executor.Trim().ToLowerInvariant();
        agent.TracesEnabled = input.TracesEnabled ?? false;
        agent.TraceWaitSeconds = Math.Clamp(input.TraceWaitSeconds ?? 15, 1, 45);
        agent.Mcp = input.Mcp ?? new McpSettings();
        agent.TimeoutSeconds = Math.Clamp(input.TimeoutSeconds ?? 120, 1, 1800);
        return agent;
    }

    // ---------- Datasets ----------

    private static void MapDatasets(RouteGroupBuilder g)
    {
        g.MapGet("/", async (EvalDbContext db) =>
            (await db.Datasets.OrderBy(d => d.Name).ToListAsync()).Select(d => new
            {
                d.Id,
                d.Name,
                d.Columns,
                rowCount = d.Rows.Count,
                d.SourceUrl,
                d.SourceFileName,
                d.UpdatedAt,
            }));

        g.MapGet("/{id:int}", async (int id, EvalDbContext db) =>
            await db.Datasets.FindAsync(id) is Dataset d ? Results.Ok(d) : Results.NotFound());

        g.MapPost("/archivo", async ([FromForm] string? nombre, IFormFile archivo, EvalDbContext db) =>
        {
            ImportedTable table;
            try
            {
                await using Stream stream = archivo.OpenReadStream();
                table = DatasetImporter.FromFile(archivo.FileName, stream);
            }
            catch (Exception ex) when (ex is ImportException or CsvHelper.CsvHelperException or InvalidDataException)
            {
                return Bad(ex is ImportException ? ex.Message : $"No se pudo leer el archivo: {ex.Message}");
            }
            var dataset = new Dataset
            {
                Name = string.IsNullOrWhiteSpace(nombre) ? Path.GetFileNameWithoutExtension(archivo.FileName) : nombre.Trim(),
                SourceFileName = archivo.FileName,
                Columns = table.Columns,
                Rows = table.Rows,
            };
            db.Datasets.Add(dataset);
            await db.SaveChangesAsync();
            return Results.Created($"/api/datasets/{dataset.Id}", new { dataset.Id });
        }).DisableAntiforgery();

        g.MapPut("/{id:int}/archivo", async (int id, IFormFile archivo, EvalDbContext db) =>
        {
            if (await db.Datasets.FindAsync(id) is not Dataset dataset)
            {
                return Results.NotFound();
            }
            try
            {
                await using Stream stream = archivo.OpenReadStream();
                ImportedTable table = DatasetImporter.FromFile(archivo.FileName, stream);
                (dataset.Columns, dataset.Rows) = (table.Columns, table.Rows);
            }
            catch (Exception ex) when (ex is ImportException or CsvHelper.CsvHelperException or InvalidDataException)
            {
                return Bad(ex is ImportException ? ex.Message : $"No se pudo leer el archivo: {ex.Message}");
            }
            dataset.SourceFileName = archivo.FileName;
            dataset.SourceUrl = null;
            dataset.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
            return Results.Ok(new { dataset.Id });
        }).DisableAntiforgery();

        g.MapPost("/enlace", async (DatasetLinkInput input, DatasetImporter importer, EvalDbContext db, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(input.Name))
            {
                return Bad("Ponle un nombre al dataset.");
            }
            ImportedTable table;
            try
            {
                table = await importer.FromUrlAsync(input.Url, ct);
            }
            catch (Exception ex) when (ex is ImportException or HttpRequestException or CsvHelper.CsvHelperException)
            {
                return Bad(ex is ImportException ? ex.Message : $"No se pudo leer el enlace: {ex.Message}");
            }
            var dataset = new Dataset { Name = input.Name.Trim(), SourceUrl = input.Url.Trim(), Columns = table.Columns, Rows = table.Rows };
            db.Datasets.Add(dataset);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/datasets/{dataset.Id}", new { dataset.Id });
        });

        g.MapPost("/{id:int}/recargar", async (int id, DatasetImporter importer, EvalDbContext db, CancellationToken ct) =>
        {
            if (await db.Datasets.FindAsync([id], ct) is not Dataset dataset)
            {
                return Results.NotFound();
            }
            if (string.IsNullOrWhiteSpace(dataset.SourceUrl))
            {
                return Bad("Este dataset se cargó desde un archivo. Sube el archivo de nuevo para actualizarlo.");
            }
            try
            {
                ImportedTable table = await importer.FromUrlAsync(dataset.SourceUrl, ct);
                (dataset.Columns, dataset.Rows) = (table.Columns, table.Rows);
            }
            catch (Exception ex) when (ex is ImportException or HttpRequestException or CsvHelper.CsvHelperException)
            {
                return Bad(ex is ImportException ? ex.Message : $"No se pudo leer el enlace: {ex.Message}");
            }
            dataset.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { dataset.Id, rowCount = dataset.Rows.Count });
        });

        g.MapPost("/generar", async (GenerateCasesInput input, IJudgeProvider judgeProvider, EvalDbContext db, CancellationToken ct) =>
        {
            if (judgeProvider.Get() is not { } judge)
            {
                return Bad("Para generar casos con IA hay que configurar el juez IA (ver README, sección Juez IA).");
            }
            if (string.IsNullOrWhiteSpace(input.Name) || string.IsNullOrWhiteSpace(input.Description))
            {
                return Bad("Ponle un nombre y describe qué hace el agente.");
            }
            ImportedTable table;
            try
            {
                table = await CaseGenerator.GenerateAsync(judge, input, ct);
            }
            catch (ImportException ex)
            {
                return Bad(ex.Message);
            }
            var dataset = new Dataset { Name = input.Name.Trim(), SourceFileName = "generado con IA", Columns = table.Columns, Rows = table.Rows };
            db.Datasets.Add(dataset);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/datasets/{dataset.Id}", new { dataset.Id, rowCount = dataset.Rows.Count });
        });

        g.MapGet("/{id:int}/excel", async (int id, EvalDbContext db) =>
            await db.Datasets.FindAsync(id) is Dataset d
                ? Results.File(RunReport.DatasetToExcel(d), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", $"{string.Concat(d.Name.Split(Path.GetInvalidFileNameChars()))}.xlsx")
                : Results.NotFound());

        g.MapPut("/{id:int}", async (int id, RenameInput input, EvalDbContext db) =>
        {
            if (await db.Datasets.FindAsync(id) is not Dataset dataset)
            {
                return Results.NotFound();
            }
            if (string.IsNullOrWhiteSpace(input.Name))
            {
                return Bad("El nombre no puede quedar vacío.");
            }
            dataset.Name = input.Name.Trim();
            await db.SaveChangesAsync();
            return Results.Ok(new { dataset.Id });
        });

        g.MapDelete("/{id:int}", async (int id, EvalDbContext db) =>
        {
            if (await db.Datasets.FindAsync(id) is not Dataset dataset)
            {
                return Results.NotFound();
            }
            if (await db.Evaluations.AnyAsync(e => e.DatasetId == id))
            {
                return Conflict("Hay evaluaciones que usan este dataset. Bórralas o cámbiales el dataset primero.");
            }
            db.Datasets.Remove(dataset);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        g.MapGet("/plantilla.csv", () => Results.File(
            System.Text.Encoding.UTF8.GetPreamble().Concat(System.Text.Encoding.UTF8.GetBytes(
                "id,input,respuesta_esperada,debe_contener,no_debe_contener,criterio\n" +
                "1,¿Cuál es el horario de atención?,Atendemos de lunes a viernes de 8:00 a 18:00.,lunes|18:00,,Da el horario sin inventar canales adicionales.\n")).ToArray(),
            "text/csv", "plantilla-casos.csv"));
    }

    // ---------- Evaluaciones ----------

    private static void MapEvaluations(RouteGroupBuilder g)
    {
        g.MapGet("/", async (EvalDbContext db) =>
        {
            var evaluations = await db.Evaluations.OrderBy(e => e.Name).ToListAsync();
            var agents = await db.Agents.ToDictionaryAsync(a => a.Id, a => a.Name);
            var datasets = await db.Datasets.Select(d => new { d.Id, d.Name }).ToDictionaryAsync(d => d.Id, d => d.Name);
            var lastRuns = (await db.Runs.ToListAsync())
                .GroupBy(r => r.EvaluationId)
                .ToDictionary(gr => gr.Key, gr => gr.OrderByDescending(r => r.Id).First());
            return evaluations.Select(e => new
            {
                e.Id,
                e.Name,
                e.AgentId,
                agentName = agents.GetValueOrDefault(e.AgentId),
                e.DatasetId,
                datasetName = datasets.GetValueOrDefault(e.DatasetId),
                checkCount = e.Checks.Count,
                variantCount = e.Variants.Count,
                lastRun = lastRuns.TryGetValue(e.Id, out Run? r) ? RunListItem(r) : null,
            });
        });

        g.MapGet("/{id:int}", async (int id, EvalDbContext db) =>
        {
            if (await db.Evaluations.FindAsync(id) is not Evaluation e)
            {
                return Results.NotFound();
            }
            var runs = await db.Runs.Where(r => r.EvaluationId == id).OrderByDescending(r => r.Id).Take(50).ToListAsync();
            var comparisons = await db.Comparisons.Where(c => c.EvaluationId == id).OrderByDescending(c => c.Id).Take(20).ToListAsync();
            var comparisonIds = comparisons.Select(c => c.Id).ToList();
            var comparisonRuns = (await db.Runs.Where(r => r.ComparisonId != null && comparisonIds.Contains(r.ComparisonId.Value)).ToListAsync())
                .GroupBy(r => r.ComparisonId!.Value)
                .ToDictionary(gr => gr.Key, gr => gr.OrderBy(r => r.Id).Select(RunListItem).ToList());
            return Results.Ok(new
            {
                evaluation = e,
                runs = runs.Select(RunListItem),
                comparisons = comparisons.Select(c => new { c.Id, c.Note, c.CreatedAt, runs = comparisonRuns.GetValueOrDefault(c.Id) ?? [] }),
            });
        });

        g.MapPost("/", async (EvaluationInput input, EvalDbContext db) =>
        {
            if (await ValidateAsync(input, db) is string error)
            {
                return Bad(error);
            }
            var evaluation = Apply(new Evaluation(), input);
            db.Evaluations.Add(evaluation);
            await db.SaveChangesAsync();
            return Results.Created($"/api/evaluaciones/{evaluation.Id}", evaluation);
        });

        g.MapPut("/{id:int}", async (int id, EvaluationInput input, EvalDbContext db) =>
        {
            if (await db.Evaluations.FindAsync(id) is not Evaluation evaluation)
            {
                return Results.NotFound();
            }
            if (await ValidateAsync(input, db) is string error)
            {
                return Bad(error);
            }
            Apply(evaluation, input);
            await db.SaveChangesAsync();
            return Results.Ok(evaluation);
        });

        g.MapDelete("/{id:int}", async (int id, EvalDbContext db) =>
        {
            if (await db.Evaluations.FindAsync(id) is not Evaluation evaluation)
            {
                return Results.NotFound();
            }
            if (await db.Runs.AnyAsync(r => r.EvaluationId == id && (r.Status == RunStatus.Queued || r.Status == RunStatus.Running)))
            {
                return Conflict("Hay una corrida en curso de esta evaluación. Cancélala primero.");
            }
            var runIds = await db.Runs.Where(r => r.EvaluationId == id).Select(r => r.Id).ToListAsync();
            await db.RunResults.Where(r => runIds.Contains(r.RunId)).ExecuteDeleteAsync();
            await db.RunTasks.Where(t => runIds.Contains(t.RunId)).ExecuteDeleteAsync();
            await db.Runs.Where(r => r.EvaluationId == id).ExecuteDeleteAsync();
            await db.Comparisons.Where(c => c.EvaluationId == id).ExecuteDeleteAsync();
            db.Evaluations.Remove(evaluation);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        g.MapPost("/{id:int}/ejecutar", async (int id, RunInput? input, EvalDbContext db, RunQueue queue) =>
        {
            if (await db.Evaluations.FindAsync(id) is not Evaluation evaluation)
            {
                return Results.NotFound();
            }
            Variant? variant = null;
            if (!string.IsNullOrWhiteSpace(input?.Variant))
            {
                variant = evaluation.Variants.FirstOrDefault(v => v.Name.Equals(input.Variant.Trim(), StringComparison.OrdinalIgnoreCase));
                if (variant is null)
                {
                    return Bad($"La evaluación no tiene una variante llamada '{input.Variant.Trim()}'.");
                }
            }
            var (runs, error) = await PrepareRunsAsync(evaluation, [variant], input?.Note, null, db);
            if (error is not null)
            {
                return Bad(error);
            }
            await db.SaveChangesAsync();
            await queue.EnqueueAsync(runs[0].Id);
            return Results.Accepted($"/api/corridas/{runs[0].Id}", new { runId = runs[0].Id });
        });

        // Corre las mismas pruebas con cada variante (una corrida por variante) para verlas lado a lado.
        g.MapPost("/{id:int}/comparar", async (int id, CompareInput? input, EvalDbContext db, RunQueue queue) =>
        {
            if (await db.Evaluations.FindAsync(id) is not Evaluation evaluation)
            {
                return Results.NotFound();
            }
            List<Variant> chosen = evaluation.Variants;
            if (input?.Variants is { Count: > 0 } names)
            {
                string? missing = names.FirstOrDefault(n => !evaluation.Variants.Any(v => v.Name.Equals(n.Trim(), StringComparison.OrdinalIgnoreCase)));
                if (missing is not null)
                {
                    return Bad($"La evaluación no tiene una variante llamada '{missing}'.");
                }
                chosen = evaluation.Variants.Where(v => names.Any(n => v.Name.Equals(n.Trim(), StringComparison.OrdinalIgnoreCase))).ToList();
            }
            if (chosen.Count < 2)
            {
                return Bad("Para comparar se necesitan al menos dos variantes. Agrégalas al editar la evaluación (sección «Variantes a comparar»).");
            }
            Dataset? dataset = await db.Datasets.FindAsync(evaluation.DatasetId);
            var comparison = new Comparison
            {
                EvaluationId = evaluation.Id,
                EvaluationName = evaluation.Name,
                DatasetName = dataset?.Name ?? "",
                Note = string.IsNullOrWhiteSpace(input?.Note) ? null : input.Note.Trim(),
            };
            db.Comparisons.Add(comparison);
            await db.SaveChangesAsync();
            var (runs, error) = await PrepareRunsAsync(evaluation, chosen.Cast<Variant?>().ToList(), input?.Note, comparison.Id, db);
            if (error is not null)
            {
                db.Comparisons.Remove(comparison);
                await db.SaveChangesAsync();
                return Bad(error);
            }
            await db.SaveChangesAsync();
            foreach (Run run in runs)
            {
                await queue.EnqueueAsync(run.Id);
            }
            return Results.Accepted($"/api/comparativas/{comparison.Id}", new { comparisonId = comparison.Id, runIds = runs.Select(r => r.Id) });
        });
    }

    // Una corrida por variante (null = el agente de la evaluación sin parámetros). Las deja agregadas sin guardar.
    private static async Task<(List<Run> Runs, string? Error)> PrepareRunsAsync(Evaluation evaluation, List<Variant?> variants, string? note, int? comparisonId, EvalDbContext db)
    {
        Dataset? dataset = await db.Datasets.FindAsync(evaluation.DatasetId);
        if (dataset is null)
        {
            return ([], "El dataset de esta evaluación ya no existe.");
        }
        if (dataset.Rows.Count == 0)
        {
            return ([], "El dataset no tiene casos.");
        }
        var runs = new List<Run>();
        foreach (Variant? variant in variants)
        {
            int agentId = variant?.AgentId ?? evaluation.AgentId;
            if (await db.Agents.FindAsync(agentId) is not Agent agent)
            {
                return ([], variant is null ? "El agente de esta evaluación ya no existe." : $"El agente de la variante '{variant.Name}' ya no existe.");
            }
            runs.Add(new Run
            {
                EvaluationId = evaluation.Id,
                EvaluationName = evaluation.Name,
                AgentId = agent.Id,
                AgentName = agent.Name,
                DatasetName = dataset.Name,
                Variant = variant?.Name,
                Parameters = new Dictionary<string, string>(variant?.Parameters ?? []),
                ComparisonId = comparisonId,
                Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim(),
                Checks = evaluation.Checks,
                Total = dataset.Rows.Count * Math.Max(1, evaluation.Repetitions),
            });
        }
        db.Runs.AddRange(runs);
        return (runs, null);
    }

    private static async Task<string?> ValidateAsync(EvaluationInput input, EvalDbContext db)
    {
        if (string.IsNullOrWhiteSpace(input.Name))
        {
            return "Ponle un nombre a la evaluación.";
        }
        if (!await db.Agents.AnyAsync(a => a.Id == input.AgentId))
        {
            return "Elige un agente.";
        }
        if (await db.Datasets.FindAsync(input.DatasetId) is not Dataset dataset)
        {
            return "Elige un dataset.";
        }
        if (!dataset.Columns.Contains(input.InputColumn))
        {
            return "Elige qué columna del dataset se manda al agente como entrada.";
        }
        if (input.Checks is not { Count: > 0 })
        {
            return "Agrega al menos una comparación.";
        }
        foreach (var (check, i) in input.Checks.Select((c, i) => (c, i + 1)))
        {
            CheckTypeInfo? info = CheckTypes.Find(check.Type);
            if (info is null)
            {
                return $"Comparación {i}: tipo desconocido '{check.Type}'.";
            }
            if (check.ExpectedColumn is { Length: > 0 } column && !dataset.Columns.Contains(column))
            {
                return $"Comparación {i} ({info.Label}): la columna '{column}' no está en el dataset.";
            }
            if (info.NeedsExpected && string.IsNullOrWhiteSpace(check.ExpectedColumn) && string.IsNullOrWhiteSpace(check.ExpectedValue))
            {
                return $"Comparación {i} ({info.Label}): elige la columna con lo esperado o escribe un valor fijo.";
            }
        }
        if (input.Repetitions is < 1 or > 10)
        {
            return "Las repeticiones deben estar entre 1 y 10.";
        }
        return await ValidateVariantsAsync(input, db);
    }

    private static async Task<string?> ValidateVariantsAsync(EvaluationInput input, EvalDbContext db)
    {
        List<Variant> variants = input.Variants ?? [];
        if (variants.Count > 10)
        {
            return "Se pueden comparar hasta 10 variantes.";
        }
        var seen = new Dictionary<string, Variant>(StringComparer.OrdinalIgnoreCase);
        foreach (var (variant, i) in variants.Select((v, i) => (v, i + 1)))
        {
            string name = variant.Name?.Trim() ?? "";
            if (name.Length is 0 or > 60)
            {
                return $"Variante {i}: ponle un nombre corto (hasta 60 letras), por ejemplo «gpt-5-mini + prompt v2».";
            }
            if (seen.ContainsKey(name))
            {
                return $"Hay dos variantes llamadas «{name}».";
            }
            int agentId = variant.AgentId ?? input.AgentId;
            if (await db.Agents.FindAsync(agentId) is not Agent agent)
            {
                return $"Variante «{name}»: el agente elegido no existe.";
            }
            var parameters = (variant.Parameters ?? []).Where(p => !string.IsNullOrWhiteSpace(p.Key)).ToDictionary(p => p.Key.Trim(), p => p.Value ?? "");
            foreach (string key in parameters.Keys)
            {
                if (!ParameterName().IsMatch(key) || key.Equals("input", StringComparison.OrdinalIgnoreCase))
                {
                    return $"Variante «{name}»: «{key}» no sirve como nombre de parámetro. Usa letras, números o _ (por ejemplo modelo, prompt, top_k) y no uses «input».";
                }
            }
            // Un parámetro que el agente no usa no cambia nada: la comparación saldría igual sin que se note.
            if (agent.Kind != AgentKinds.Code)
            {
                HashSet<string> used = UsedPlaceholders(agent);
                string? unused = parameters.Keys.FirstOrDefault(k => !used.Contains(k) && !(agent.Kind == AgentKinds.McpModel && k.Equals("temperatura", StringComparison.OrdinalIgnoreCase)));
                if (unused is not null)
                {
                    return $"Variante «{name}»: el agente «{agent.Name}» no usa el parámetro «{unused}». Escribe {{{{{unused}}}}} en su configuración (por ejemplo en el cuerpo o en el prompt) o quita el parámetro.";
                }
            }
            Variant? twin = seen.Values.FirstOrDefault(o => (o.AgentId ?? input.AgentId) == agentId
                && o.Parameters.Count == parameters.Count
                && o.Parameters.All(p => parameters.TryGetValue(p.Key, out string? v) && v == p.Value));
            if (twin is not null)
            {
                return $"Las variantes «{twin.Name}» y «{name}» son iguales: mismo agente y mismos parámetros.";
            }
            seen[name] = new Variant { Name = name, AgentId = variant.AgentId, Parameters = parameters };
        }
        return null;
    }

    // Marcadores {{...}} que aparecen en la configuración del agente.
    private static HashSet<string> UsedPlaceholders(Agent agent) =>
        new[] { agent.Url }
            .Concat(agent.Kind switch
            {
                AgentKinds.McpDirect => [agent.Mcp.ToolTemplate, agent.Mcp.ArgumentsTemplate],
                AgentKinds.McpModel => [agent.Mcp.SystemPrompt, agent.Mcp.ModelName, agent.Mcp.ModelEndpoint, agent.Mcp.AllowedTools],
                _ => new[] { agent.BodyTemplate },
            })
            .Concat(agent.Headers.Values)
            .SelectMany(Templates.Placeholders)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    [System.Text.RegularExpressions.GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]{0,49}$")]
    private static partial System.Text.RegularExpressions.Regex ParameterName();

    private static Evaluation Apply(Evaluation evaluation, EvaluationInput input)
    {
        evaluation.Name = input.Name.Trim();
        evaluation.AgentId = input.AgentId;
        evaluation.DatasetId = input.DatasetId;
        evaluation.InputColumn = input.InputColumn;
        evaluation.Checks = input.Checks.Select(c => new CheckDefinition
        {
            Type = c.Type,
            Label = string.IsNullOrWhiteSpace(c.Label) ? null : c.Label.Trim(),
            // Solo se guarda lo esperado en los tipos que lo usan.
            ExpectedColumn = CheckTypes.Find(c.Type)?.NeedsExpected != true || string.IsNullOrWhiteSpace(c.ExpectedColumn) ? null : c.ExpectedColumn,
            ExpectedValue = CheckTypes.Find(c.Type)?.NeedsExpected != true || !string.IsNullOrWhiteSpace(c.ExpectedColumn) || string.IsNullOrWhiteSpace(c.ExpectedValue) ? null : c.ExpectedValue,
            ResponsePath = string.IsNullOrWhiteSpace(c.ResponsePath) ? null : c.ResponsePath.Trim(),
            Threshold = c.Threshold,
            Blocking = c.Blocking,
        }).ToList();
        evaluation.Repetitions = input.Repetitions ?? 1;
        evaluation.Variants = (input.Variants ?? []).Select(v => new Variant
        {
            Name = v.Name.Trim(),
            AgentId = v.AgentId == input.AgentId ? null : v.AgentId,
            Parameters = (v.Parameters ?? []).Where(p => !string.IsNullOrWhiteSpace(p.Key)).ToDictionary(p => p.Key.Trim(), p => p.Value ?? ""),
        }).ToList();
        return evaluation;
    }

    // ---------- Corridas ----------

    private static object RunListItem(Run r) => new
    {
        r.Id,
        r.EvaluationId,
        r.EvaluationName,
        r.AgentName,
        r.DatasetName,
        r.AgentId,
        r.Variant,
        r.Parameters,
        r.ComparisonId,
        r.Note,
        r.Executor,
        r.Status,
        r.Error,
        r.Total,
        r.Completed,
        r.Passed,
        r.Failed,
        r.Errors,
        passRate = r.Completed == 0 ? (double?)null : (double)r.Passed / r.Completed,
        r.CreatedAt,
        r.StartedAt,
        r.FinishedAt,
    };

    private static void MapRuns(RouteGroupBuilder g)
    {
        g.MapGet("/", async (EvalDbContext db, int? limite) =>
            (await db.Runs.OrderByDescending(r => r.Id).Take(Math.Clamp(limite ?? 20, 1, 200)).ToListAsync()).Select(RunListItem));

        g.MapGet("/{id:int}", async (int id, EvalDbContext db) =>
        {
            if (await db.Runs.FindAsync(id) is not Run run)
            {
                return Results.NotFound();
            }
            var (summary, _) = await SummaryAsync(run, db);
            return Results.Ok(new { run = RunListItem(run), checks = run.Checks, summary });
        });

        g.MapGet("/{id:int}/resultados", async (int id, EvalDbContext db) =>
            await db.RunResults.Where(r => r.RunId == id).OrderBy(r => r.RowIndex).ThenBy(r => r.Attempt).ToListAsync());

        g.MapPost("/{id:int}/cancelar", async (int id, EvalDbContext db, RunQueue queue, RemoteTasks tasks) =>
        {
            if (await db.Runs.FindAsync(id) is not Run run)
            {
                return Results.NotFound();
            }
            await CancelRunAsync(run, db, queue, tasks);
            return Results.Accepted();
        });

        g.MapDelete("/{id:int}", async (int id, EvalDbContext db) =>
        {
            if (await db.Runs.FindAsync(id) is not Run run)
            {
                return Results.NotFound();
            }
            if (run.Status is RunStatus.Queued or RunStatus.Running)
            {
                return Conflict("La corrida sigue en curso. Cancélala primero.");
            }
            await db.RunResults.Where(r => r.RunId == id).ExecuteDeleteAsync();
            await db.RunTasks.Where(t => t.RunId == id).ExecuteDeleteAsync();
            db.Runs.Remove(run);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        g.MapGet("/{id:int}/excel", async (int id, EvalDbContext db) =>
        {
            if (await db.Runs.FindAsync(id) is not Run run)
            {
                return Results.NotFound();
            }
            var (summary, results) = await SummaryAsync(run, db);
            List<string> columns = (await db.Evaluations.FindAsync(run.EvaluationId) is Evaluation e && await db.Datasets.FindAsync(e.DatasetId) is Dataset d)
                ? d.Columns
                : results.SelectMany(r => r.Row.Keys).Distinct().ToList();
            byte[] file = RunReport.ToExcel(run, results, summary, columns);
            string name = $"corrida-{run.Id}-{run.CreatedAt.ToLocalTime():yyyyMMdd-HHmm}.xlsx";
            return Results.File(file, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", name);
        });
    }

    private static async Task CancelRunAsync(Run run, EvalDbContext db, RunQueue queue, RemoteTasks tasks)
    {
        if (run.Executor is not null && run.Status == RunStatus.Running)
        {
            await tasks.CancelRunAsync(db, run.Id, CancellationToken.None);
            run.Status = RunStatus.Cancelled;
            run.FinishedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
        }
        else if (run.Status == RunStatus.Queued)
        {
            run.Status = RunStatus.Cancelled;
            run.FinishedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
        }
        else if (run.Status == RunStatus.Running)
        {
            queue.Cancel(run.Id);
        }
    }

    private static async Task<(RunSummary Summary, List<RunResult> Results)> SummaryAsync(Run run, EvalDbContext db)
    {
        var results = await db.RunResults.Where(r => r.RunId == run.Id).ToListAsync();
        Run? previous = await db.Runs
            .Where(r => r.EvaluationId == run.EvaluationId && r.Id < run.Id && r.Status == RunStatus.Completed && r.Variant == run.Variant)
            .OrderByDescending(r => r.Id)
            .FirstOrDefaultAsync();
        List<RunResult>? previousResults = previous is null ? null : await db.RunResults.Where(r => r.RunId == previous.Id).ToListAsync();
        return (RunReport.Summarize(run, results, previous, previousResults), results);
    }

    // ---------- Plantillas de agente ----------

    private static readonly AgentTemplate[] s_templates =
    [
        new("pasos", "Agente con pasos intermedios (webhook)",
            "Un agente que responde { output, intermediateSteps } (formato de agentes con herramientas, como LangChain): la respuesta, los documentos que recuperó y las herramientas que usó.",
            new AgentInput("Mi agente", "https://tu-servidor/webhook/mi-agente", "POST", new() { ["X-Eval"] = "1" },
                """{ "mensaje": "{{input}}" }""", "output", "intermediateSteps[*].observation", "intermediateSteps[*].action.tool", 120,
                ToolArgumentsPath: "intermediateSteps[*].action.toolInput")),
        new("eval-run", "Endpoint de evaluación (C# u otro)",
            "Un endpoint propio que devuelve { output, toolCalls[], ... }, como samples/AgentEndpoint de este repositorio.",
            new AgentInput("Mi agente", "http://localhost:5150/eval/run", "POST", new() { ["X-Eval-Key"] = "{{env:AGENT_EVAL_KEY}}" },
                """{ "input": "{{input}}" }""", "output", null, "toolCalls[*].name", 120, "usage.inputTokens, usage.outputTokens",
                ToolArgumentsPath: "toolCalls[*].arguments")),
        new("openai", "Prompt directo a un modelo (API compatible con OpenAI)",
            "Para evaluar un prompt sin agente: el prompt de sistema va en el cuerpo y la clave en una variable de entorno.",
            new AgentInput("Mi prompt", "https://api.openai.com/v1/chat/completions", "POST", new() { ["Authorization"] = "Bearer {{env:OPENAI_API_KEY}}" },
                """{ "model": "gpt-5-mini", "messages": [ { "role": "system", "content": "Eres un asistente de soporte. Responde en español, breve y amable." }, { "role": "user", "content": "{{input}}" } ] }""",
                "choices[0].message.content", null, "choices[0].message.tool_calls[*].function.name", 120, "usage.total_tokens",
                ToolArgumentsPath: "choices[0].message.tool_calls[*].function.arguments")),
        new("anthropic", "Prompt directo a Claude (API de Anthropic)",
            "Igual que la anterior pero con la API de Messages de Anthropic.",
            new AgentInput("Mi prompt con Claude", "https://api.anthropic.com/v1/messages", "POST",
                new() { ["x-api-key"] = "{{env:ANTHROPIC_API_KEY}}", ["anthropic-version"] = "2023-06-01" },
                """{ "model": "claude-sonnet-5-5", "max_tokens": 1024, "system": "Eres un asistente de soporte. Responde en español, breve y amable.", "messages": [ { "role": "user", "content": "{{input}}" } ] }""",
                "content[*].text", null, "content[*].name", 120, "usage.input_tokens, usage.output_tokens",
                ToolArgumentsPath: "content[*].input")),
        new("mcp", "Servidor MCP: llamar herramientas",
            "Llama una herramienta del MCP (tools/call) con los argumentos de cada caso y compara el resultado. Por defecto toma las columnas 'herramienta' y 'argumentos'.",
            new AgentInput("Mi servidor MCP", "https://tu-servidor/mcp", null, new() { ["Authorization"] = "Bearer {{env:MCP_TOKEN}}" },
                null, "", null, null, 60, Kind: AgentKinds.McpDirect, Mcp: new McpSettings())),
        new("mcp_modelo", "Servidor MCP con un modelo",
            "Un modelo recibe las herramientas del MCP y la pregunta del caso. Sirve para saber si elige la herramienta correcta, con los argumentos correctos, según los nombres y descripciones de tus herramientas.",
            new AgentInput("Modelo + mi MCP", "https://tu-servidor/mcp", null, new() { ["Authorization"] = "Bearer {{env:MCP_TOKEN}}" },
                null, "", null, null, 120, Kind: AgentKinds.McpModel, Mcp: new McpSettings
                {
                    SystemPrompt = "Eres un asistente. Usa las herramientas disponibles cuando las necesites y responde en español.",
                    ModelEndpoint = "https://api.openai.com/v1/",
                    ModelName = "",
                    ModelApiKey = "{{env:OPENAI_API_KEY}}",
                })),
    ];
}
