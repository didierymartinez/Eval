using EvalPlatform.Domain;
using EvalPlatform.Services;
using Microsoft.Extensions.Options;

namespace EvalPlatform.Api;

public sealed record ClaimInput(string Group, string? Worker, string? Mode, int? Max, int? WaitSeconds);

// API para los ejecutores que corren dentro de otra red o en otra tecnología:
//   POST /api/ejecutores/tomar      pide casos pendientes de su grupo
//   POST /api/ejecutores/resultado  devuelve la respuesta del agente para un caso
// Solo hacen conexiones salientes hacia la plataforma; no hay que abrir puertos en su red.
public static class ExecutorEndpoints
{
    public static void MapExecutorApi(this WebApplication app)
    {
        RouteGroupBuilder g = app.MapGroup("/api/ejecutores");

        // Estado para la interfaz (sin token): quién está conectado y cuántos casos esperan.
        g.MapGet("/estado", async (RemoteTasks tasks, IOptions<ExecutorOptions> options, CancellationToken ct) =>
        {
            var queue = await tasks.QueueByGroupAsync(ct);
            var seen = tasks.Seen.GroupBy(s => s.Group).ToDictionary(x => x.Key, x => x.ToList());
            return new
            {
                habilitado = !string.IsNullOrWhiteSpace(options.Value.Token),
                grupos = queue.Keys.Union(seen.Keys).Order().Select(group => new
                {
                    grupo = group,
                    pendientes = queue.GetValueOrDefault(group).Pending,
                    enCurso = queue.GetValueOrDefault(group).Claimed,
                    ejecutores = seen.GetValueOrDefault(group, []).OrderByDescending(s => s.LastSeen).Select(s => new
                    {
                        nombre = s.Worker,
                        tipo = s.Mode,
                        ultimaVez = s.LastSeen,
                        // Un ejecutor que pide casos vuelve a preguntar a lo sumo cada ~30 s.
                        conectado = DateTimeOffset.UtcNow - s.LastSeen < TimeSpan.FromSeconds(60),
                    }),
                }),
            };
        });

        RouteGroupBuilder secured = g.MapGroup("").AddEndpointFilter(async (context, next) =>
        {
            string? expected = context.HttpContext.RequestServices.GetRequiredService<IOptions<ExecutorOptions>>().Value.Token;
            if (string.IsNullOrWhiteSpace(expected))
            {
                return Results.Json(new { error = "La API de ejecutores está apagada: configura Ejecutores__Token en el servidor de la plataforma." }, statusCode: 503);
            }
            if (!context.HttpContext.RequestServices.GetRequiredService<IOptions<ExecutorOptions>>().Value.Accepts(context.HttpContext.Request))
            {
                return Results.Json(new { error = "Token de ejecutor inválido." }, statusCode: 401);
            }
            return await next(context);
        });

        secured.MapPost("/tomar", async (ClaimInput input, RemoteTasks tasks, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(input.Group) || !RemoteTasks.IsValidGroup(input.Group.Trim().ToLowerInvariant()))
            {
                return Results.BadRequest(new { error = "El grupo debe tener letras minúsculas, números, - o _ (ej. red-finanzas)." });
            }
            string mode = input.Mode == "generico" ? "generico" : "codigo";
            string worker = string.IsNullOrWhiteSpace(input.Worker) ? "sin-nombre" : input.Worker.Trim();
            List<ExecutorTask> claimed = await tasks.ClaimAsync(input.Group, worker, mode, input.Max ?? 1, TimeSpan.FromSeconds(Math.Clamp(input.WaitSeconds ?? 0, 0, 25)), ct);
            return Results.Ok(claimed);
        });

        secured.MapPost("/resultado", async (ExecutorResult input, HttpRequest request, RemoteTasks tasks, CancellationToken ct) =>
        {
            try
            {
                RunResult result = await tasks.CompleteAsync(input, request.Headers["X-Ejecutor-Nombre"].FirstOrDefault() ?? "sin-nombre", ct);
                return Results.Ok(new
                {
                    resultado = result.Outcome,
                    comparaciones = result.Checks.Select(c => new { c.Label, c.Status, c.Reason }),
                });
            }
            catch (TaskNotFoundException ex)
            {
                return Results.Conflict(new { error = ex.Message });
            }
        });
    }
}
