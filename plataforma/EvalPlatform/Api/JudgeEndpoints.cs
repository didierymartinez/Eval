using System.Diagnostics;
using EvalPlatform.Data;
using EvalPlatform.Domain;
using EvalPlatform.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;

namespace EvalPlatform.Api;

// ApiKey: vacío = se conserva la guardada (ClearApiKey la borra). Puede ser {{env:VARIABLE}}.
// Si cambia el endpoint hay que volver a escribirla: la clave guardada nunca viaja a un endpoint nuevo.
public sealed record JudgeInput(string Name, string? Endpoint, string Model, string? ApiKey, long? MonthlyTokenLimit, bool? ClearApiKey = null);

// Id: para probar un juez guardado con su clave sin volver a escribirla.
public sealed record JudgeTestInput(int? Id, string? Endpoint, string? Model, string? ApiKey);

// Jueces IA de cada equipo, para que cada uno pague los tokens de sus evaluaciones.
public static partial class ApiEndpoints
{
    private static void MapJudges(RouteGroupBuilder g)
    {
        g.MapGet("/", async (EvalDbContext db, JudgeResolver resolver, IJudgeProvider server, Microsoft.Extensions.Options.IOptions<JudgeOptions> options, JudgeSecrets secrets) =>
        {
            string month = JudgeBudget.CurrentMonth;
            var usage = await db.JudgeUsages.Where(u => u.Month == month).ToDictionaryAsync(u => u.JudgeKey);
            var evaluations = await db.Evaluations.Select(e => new { e.Id, e.JudgeId }).ToListAsync();
            var judges = await db.Judges.OrderBy(j => j.Name).ToListAsync();
            return new
            {
                mes = month,
                servidor = new
                {
                    nombre = resolver.ServerName,
                    modelo = server.ModelName,
                    configurado = server.Get() is not null,
                    permitido = resolver.ServerAllowed,
                    limiteTokensMes = options.Value.LimiteTokensMes,
                    tokensMes = usage.GetValueOrDefault(JudgeResolver.ServerKey)?.Tokens ?? 0,
                    llamadasMes = usage.GetValueOrDefault(JudgeResolver.ServerKey)?.Calls ?? 0,
                    evaluaciones = evaluations.Count(e => e.JudgeId is null),
                },
                jueces = judges.Select(j => new
                {
                    j.Id,
                    j.Name,
                    j.Endpoint,
                    j.Model,
                    claveGuardada = secrets.Hint(j.ProtectedApiKey),
                    j.MonthlyTokenLimit,
                    tokensMes = usage.GetValueOrDefault(JudgeResolver.KeyOf(j.Id))?.Tokens ?? 0,
                    llamadasMes = usage.GetValueOrDefault(JudgeResolver.KeyOf(j.Id))?.Calls ?? 0,
                    evaluaciones = evaluations.Count(e => e.JudgeId == j.Id),
                    j.UpdatedAt,
                }),
            };
        });

        g.MapPost("/", async (JudgeInput input, EvalDbContext db, JudgeSecrets secrets) =>
        {
            if (ValidateJudge(input) is string error)
            {
                return Bad(error);
            }
            var judge = ApplyJudge(new Judge(), input, secrets);
            db.Judges.Add(judge);
            await db.SaveChangesAsync();
            return Results.Created($"/api/jueces/{judge.Id}", new { judge.Id, judge.Name });
        });

        g.MapPut("/{id:int}", async (int id, JudgeInput input, EvalDbContext db, JudgeSecrets secrets, JudgeResolver resolver) =>
        {
            if (await db.Judges.FindAsync(id) is not Judge judge)
            {
                return Results.NotFound();
            }
            if (ValidateJudge(input) is string error)
            {
                return Bad(error);
            }
            if (judge.ProtectedApiKey is not null && string.IsNullOrWhiteSpace(input.ApiKey) && input.ClearApiKey != true
                && !SameEndpoint(judge.Endpoint, input.Endpoint))
            {
                return Bad("Cambiaste el endpoint: vuelve a escribir la clave. La clave guardada no se envía a un endpoint distinto.");
            }
            ApplyJudge(judge, input, secrets);
            await db.SaveChangesAsync();
            resolver.Forget(id);
            return Results.Ok(new { judge.Id, judge.Name });
        });

        g.MapDelete("/{id:int}", async (int id, EvalDbContext db, JudgeResolver resolver) =>
        {
            if (await db.Judges.FindAsync(id) is not Judge judge)
            {
                return Results.NotFound();
            }
            if (await db.Evaluations.AnyAsync(e => e.JudgeId == id))
            {
                return Conflict("Hay evaluaciones que usan este juez. Cámbiales el juez primero.");
            }
            db.Judges.Remove(judge);
            await db.SaveChangesAsync();
            resolver.Forget(id);
            return Results.NoContent();
        });

        // Una llamada corta para saber si el endpoint, el modelo y la clave funcionan.
        g.MapPost("/probar", async (JudgeTestInput input, EvalDbContext db, JudgeSecrets secrets, IJudgeClientFactory clients, JudgeBudget budget, CancellationToken ct) =>
        {
            Judge? saved = input.Id is int id ? await db.Judges.FindAsync([id], ct) : null;
            string? model = string.IsNullOrWhiteSpace(input.Model) ? saved?.Model : input.Model.Trim();
            if (string.IsNullOrWhiteSpace(model))
            {
                return Bad("Escribe el nombre del modelo.");
            }
            string? endpoint = string.IsNullOrWhiteSpace(input.Endpoint) ? saved?.Endpoint : input.Endpoint.Trim();
            string? apiKey = input.ApiKey;
            if (string.IsNullOrWhiteSpace(apiKey) && saved?.ProtectedApiKey is not null)
            {
                // La clave guardada solo se usa con el endpoint guardado.
                if (!SameEndpoint(saved.Endpoint, endpoint))
                {
                    return Bad("Para probar con otro endpoint, escribe la clave. La clave guardada no se envía a un endpoint distinto.");
                }
                if (!secrets.TryUnprotect(saved.ProtectedApiKey, out apiKey))
                {
                    return Bad("No se pudo leer la clave guardada: vuelve a escribirla.");
                }
            }

            long tokens = 0;
            IChatClient client = new UsageTrackingChatClient(
                clients.Create(endpoint, model, Templates.RenderText(apiKey ?? "", new Dictionary<string, string>())),
                t => tokens += t);
            var stopwatch = Stopwatch.StartNew();
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(30));
                ChatResponse response = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "Responde solo con la palabra OK.")], new ChatOptions { Temperature = 0 }, timeout.Token);
                if (saved is not null)
                {
                    await budget.AddAsync(JudgeResolver.KeyOf(saved.Id), tokens, 1, ct);
                }
                return Results.Ok(new { ok = true, respuesta = response.Text, tokens, latencyMs = stopwatch.ElapsedMilliseconds });
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                return Results.Ok(new { ok = false, error = ex is OperationCanceledException ? "El modelo no respondió en 30 segundos." : ex.Message, latencyMs = stopwatch.ElapsedMilliseconds });
            }
        });
    }

    private static bool SameEndpoint(string? a, string? b) =>
        string.Equals((a ?? "").Trim().TrimEnd('/'), (b ?? "").Trim().TrimEnd('/'), StringComparison.OrdinalIgnoreCase);

    private static string? ValidateJudge(JudgeInput input)
    {
        if (string.IsNullOrWhiteSpace(input.Name))
        {
            return "Ponle un nombre al juez, por ejemplo el de tu equipo.";
        }
        if (string.IsNullOrWhiteSpace(input.Model))
        {
            return "Escribe el nombre del modelo (por ejemplo gpt-5-mini o llama3.1).";
        }
        if (!string.IsNullOrWhiteSpace(input.Endpoint) && !Uri.TryCreate(input.Endpoint.Trim(), UriKind.Absolute, out _))
        {
            return "El endpoint debe ser una URL completa, por ejemplo https://api.openai.com/v1/ o http://ollama:11434/v1/.";
        }
        if (input.MonthlyTokenLimit is < 1)
        {
            return "El límite mensual debe ser mayor que cero (o vacío para no tener límite).";
        }
        return null;
    }

    private static Judge ApplyJudge(Judge judge, JudgeInput input, JudgeSecrets secrets)
    {
        judge.Name = input.Name.Trim();
        judge.Endpoint = string.IsNullOrWhiteSpace(input.Endpoint) ? null : input.Endpoint.Trim();
        judge.Model = input.Model.Trim();
        judge.MonthlyTokenLimit = input.MonthlyTokenLimit;
        if (input.ClearApiKey == true)
        {
            judge.ProtectedApiKey = null;
        }
        else if (!string.IsNullOrWhiteSpace(input.ApiKey))
        {
            judge.ProtectedApiKey = secrets.Protect(input.ApiKey.Trim());
        }
        judge.UpdatedAt = DateTimeOffset.UtcNow;
        return judge;
    }
}
