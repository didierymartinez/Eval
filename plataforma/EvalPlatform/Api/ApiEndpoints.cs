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
    int? TimeoutSeconds);

public sealed record AgentTestInput(AgentInput Agent, string Input, Dictionary<string, string>? Row);

public sealed record DatasetLinkInput(string Name, string Url);

public sealed record RenameInput(string Name);

public sealed record EvaluationInput(string Name, int AgentId, int DatasetId, string InputColumn, List<CheckDefinition> Checks, int? Repetitions);

public sealed record AgentTemplate(string Id, string Name, string Description, AgentInput Agent);

public static class ApiEndpoints
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
            if (await db.Evaluations.AnyAsync(e => e.AgentId == id))
            {
                return Conflict("Hay evaluaciones que usan este agente. Bórralas o cámbiales el agente primero.");
            }
            db.Agents.Remove(agent);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        // Prueba la conexión con un texto de ejemplo, sin guardar nada.
        g.MapPost("/probar", async (AgentTestInput test, AgentInvoker invoker, CancellationToken ct) =>
        {
            if (Validate(test.Agent) is string error)
            {
                return Bad(error);
            }
            AgentInvocation r = await invoker.InvokeAsync(Apply(new Agent(), test.Agent), test.Input ?? "", test.Row ?? [], ct);
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
                raw = r.RawResponse,
            });
        });
    }

    private static string? Validate(AgentInput input)
    {
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
        if (!string.IsNullOrWhiteSpace(input.BodyTemplate))
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
        agent.Url = input.Url.Trim();
        agent.Method = string.IsNullOrWhiteSpace(input.Method) ? "POST" : input.Method.Trim().ToUpperInvariant();
        agent.Headers = (input.Headers ?? []).Where(h => !string.IsNullOrWhiteSpace(h.Key)).ToDictionary(h => h.Key.Trim(), h => h.Value ?? "");
        agent.BodyTemplate = input.BodyTemplate ?? "";
        agent.ResponsePath = input.ResponsePath?.Trim() ?? "";
        agent.ContextPath = string.IsNullOrWhiteSpace(input.ContextPath) ? null : input.ContextPath.Trim();
        agent.ToolsPath = string.IsNullOrWhiteSpace(input.ToolsPath) ? null : input.ToolsPath.Trim();
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
            return Results.Ok(new { evaluation = e, runs = runs.Select(RunListItem) });
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
            await db.Runs.Where(r => r.EvaluationId == id).ExecuteDeleteAsync();
            db.Evaluations.Remove(evaluation);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        g.MapPost("/{id:int}/ejecutar", async (int id, EvalDbContext db, RunQueue queue) =>
        {
            if (await db.Evaluations.FindAsync(id) is not Evaluation evaluation)
            {
                return Results.NotFound();
            }
            Agent? agent = await db.Agents.FindAsync(evaluation.AgentId);
            Dataset? dataset = await db.Datasets.FindAsync(evaluation.DatasetId);
            if (agent is null || dataset is null)
            {
                return Bad("El agente o el dataset de esta evaluación ya no existen.");
            }
            if (dataset.Rows.Count == 0)
            {
                return Bad("El dataset no tiene casos.");
            }
            var run = new Run
            {
                EvaluationId = evaluation.Id,
                EvaluationName = evaluation.Name,
                AgentName = agent.Name,
                DatasetName = dataset.Name,
                Checks = evaluation.Checks,
                Total = dataset.Rows.Count * Math.Max(1, evaluation.Repetitions),
            };
            db.Runs.Add(run);
            await db.SaveChangesAsync();
            await queue.EnqueueAsync(run.Id);
            return Results.Accepted($"/api/corridas/{run.Id}", new { runId = run.Id });
        });
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
        return null;
    }

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
            ExpectedColumn = string.IsNullOrWhiteSpace(c.ExpectedColumn) ? null : c.ExpectedColumn,
            ExpectedValue = string.IsNullOrWhiteSpace(c.ExpectedColumn) && !string.IsNullOrWhiteSpace(c.ExpectedValue) ? c.ExpectedValue : null,
            ResponsePath = string.IsNullOrWhiteSpace(c.ResponsePath) ? null : c.ResponsePath.Trim(),
            Threshold = c.Threshold,
            Blocking = c.Blocking,
        }).ToList();
        evaluation.Repetitions = input.Repetitions ?? 1;
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

        g.MapPost("/{id:int}/cancelar", async (int id, EvalDbContext db, RunQueue queue) =>
        {
            if (await db.Runs.FindAsync(id) is not Run run)
            {
                return Results.NotFound();
            }
            if (run.Status == RunStatus.Queued)
            {
                run.Status = RunStatus.Cancelled;
                run.FinishedAt = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync();
            }
            else if (run.Status == RunStatus.Running)
            {
                queue.Cancel(id);
            }
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

    private static async Task<(RunSummary Summary, List<RunResult> Results)> SummaryAsync(Run run, EvalDbContext db)
    {
        var results = await db.RunResults.Where(r => r.RunId == run.Id).ToListAsync();
        Run? previous = await db.Runs
            .Where(r => r.EvaluationId == run.EvaluationId && r.Id < run.Id && r.Status == RunStatus.Completed)
            .OrderByDescending(r => r.Id)
            .FirstOrDefaultAsync();
        List<RunResult>? previousResults = previous is null ? null : await db.RunResults.Where(r => r.RunId == previous.Id).ToListAsync();
        return (RunReport.Summarize(run, results, previous, previousResults), results);
    }

    // ---------- Plantillas de agente ----------

    private static readonly AgentTemplate[] s_templates =
    [
        new("n8n", "Webhook de n8n (AI Agent)",
            "Un workflow con Webhook → AI Agent → Respond to Webhook. Activa 'Return Intermediate Steps' en el AI Agent para ver los documentos y herramientas.",
            new AgentInput("Mi agente en n8n", "https://tu-n8n/webhook/mi-agente", "POST", new() { ["X-Eval"] = "1" },
                """{ "mensaje": "{{input}}" }""", "output", "intermediateSteps[*].observation", "intermediateSteps[*].action.tool", 120)),
        new("eval-run", "Endpoint de evaluación (C# u otro)",
            "Un endpoint propio que devuelve { output, toolCalls[], ... }, como samples/AgentEndpoint de este repositorio.",
            new AgentInput("Mi agente", "http://localhost:5150/eval/run", "POST", new() { ["X-Eval-Key"] = "{{env:AGENT_EVAL_KEY}}" },
                """{ "input": "{{input}}" }""", "output", null, "toolCalls[*].name", 120)),
        new("openai", "Prompt directo a un modelo (API compatible con OpenAI)",
            "Para evaluar un prompt sin agente: el prompt de sistema va en el cuerpo y la clave en una variable de entorno.",
            new AgentInput("Mi prompt", "https://api.openai.com/v1/chat/completions", "POST", new() { ["Authorization"] = "Bearer {{env:OPENAI_API_KEY}}" },
                """{ "model": "gpt-5-mini", "messages": [ { "role": "system", "content": "Eres un asistente de soporte. Responde en español, breve y amable." }, { "role": "user", "content": "{{input}}" } ] }""",
                "choices[0].message.content", null, "choices[0].message.tool_calls[*].function.name", 120)),
        new("anthropic", "Prompt directo a Claude (API de Anthropic)",
            "Igual que la anterior pero con la API de Messages de Anthropic.",
            new AgentInput("Mi prompt con Claude", "https://api.anthropic.com/v1/messages", "POST",
                new() { ["x-api-key"] = "{{env:ANTHROPIC_API_KEY}}", ["anthropic-version"] = "2023-06-01" },
                """{ "model": "claude-sonnet-5-5", "max_tokens": 1024, "system": "Eres un asistente de soporte. Responde en español, breve y amable.", "messages": [ { "role": "user", "content": "{{input}}" } ] }""",
                "content[*].text", null, null, 120)),
    ];
}
