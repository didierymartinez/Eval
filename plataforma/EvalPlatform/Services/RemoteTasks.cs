using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using EvalPlatform.Data;
using EvalPlatform.Domain;
using Microsoft.EntityFrameworkCore;

namespace EvalPlatform.Services;

public sealed class ExecutorOptions
{
    // Token que deben enviar los ejecutores. Sin token configurado, la API de ejecutores está apagada.
    public string? Token { get; set; }
}

// Lo que recibe un ejecutor por cada caso.
public sealed record ExecutorTask(
    long TaskId,
    int RunId,
    int CaseNumber,
    int Attempt,
    string Input,
    Dictionary<string, string> Row,
    Agent Agent,
    string? TraceId);

// Lo que devuelve un ejecutor. Arguments puede venir como objeto JSON o como texto con JSON.
public sealed record ExecutorToolCall(string Name, JsonElement? Arguments, string? Result);

public sealed record ExecutorResult(
    long TaskId,
    string? Output,
    List<ExecutorToolCall>? ToolCalls,
    List<string>? Context,
    int? Tokens,
    long? LatencyMs,
    string? Error,
    string? Raw,
    int? HttpStatus);

public sealed record ExecutorSeen(string Group, string Worker, string Mode, DateTimeOffset LastSeen);

public sealed class TaskNotFoundException(string message) : Exception(message);

// Reparte los casos de las corridas remotas entre los ejecutores de cada grupo y califica lo que devuelven.
public sealed partial class RemoteTasks(IDbContextFactory<EvalDbContext> dbFactory, RunRecorder recorder, IServiceScopeFactory scopes, TraceStore traces)
{
    private readonly SemaphoreSlim _claimLock = new(1, 1);
    private readonly ConcurrentDictionary<(string Group, string Worker), ExecutorSeen> _seen = new();

    public static string GroupOf(Agent agent) => string.IsNullOrWhiteSpace(agent.Executor) ? "codigo" : agent.Executor.Trim().ToLowerInvariant();

    public static bool IsValidGroup(string group) => GroupName().IsMatch(group);

    public IReadOnlyCollection<ExecutorSeen> Seen => _seen.Values.ToList();

    // mode "generico" = el ejecutor de la plataforma (sabe llamar HTTP y MCP); "codigo" = tu propio código.
    public async Task<List<ExecutorTask>> ClaimAsync(string group, string worker, string mode, int max, TimeSpan wait, CancellationToken ct)
    {
        group = group.Trim().ToLowerInvariant();
        _seen[(group, worker)] = new ExecutorSeen(group, worker, mode, DateTimeOffset.UtcNow);
        DateTimeOffset until = DateTimeOffset.UtcNow + wait;
        while (true)
        {
            List<ExecutorTask> tasks = await TryClaimAsync(group, worker, mode, Math.Clamp(max, 1, 20), ct);
            if (tasks.Count > 0 || DateTimeOffset.UtcNow >= until)
            {
                return tasks;
            }
            // Espera corta (long polling) para no obligar al ejecutor a preguntar todo el tiempo.
            await Task.Delay(TimeSpan.FromMilliseconds(500), ct);
            _seen[(group, worker)] = new ExecutorSeen(group, worker, mode, DateTimeOffset.UtcNow);
        }
    }

    private async Task<List<ExecutorTask>> TryClaimAsync(string group, string worker, string mode, int max, CancellationToken ct)
    {
        await _claimLock.WaitAsync(ct);
        try
        {
            await using EvalDbContext db = await dbFactory.CreateDbContextAsync(ct);
            DateTimeOffset now = DateTimeOffset.UtcNow;

            // Casos prestados a un ejecutor que no respondió a tiempo: vuelven a estar disponibles.
            List<RunTask> candidates = (await db.RunTasks
                    .Where(t => t.Group == group && (t.State == RunTaskState.Pending || t.State == RunTaskState.Claimed))
                    .OrderBy(t => t.Id)
                    .Take(500)
                    .ToListAsync(ct))
                .Where(t => t.State == RunTaskState.Pending || t.LeaseUntil < now)
                .ToList();
            if (candidates.Count == 0)
            {
                return [];
            }

            var runIds = candidates.Select(t => t.RunId).Distinct().ToList();
            var runs = await db.Runs.Where(r => runIds.Contains(r.Id) && r.Status == RunStatus.Running).ToDictionaryAsync(r => r.Id, ct);
            var evaluations = await db.Evaluations.Where(e => runs.Values.Select(r => r.EvaluationId).Contains(e.Id)).ToDictionaryAsync(e => e.Id, ct);
            var agents = await db.Agents.Where(a => evaluations.Values.Select(e => e.AgentId).Contains(a.Id)).ToDictionaryAsync(a => a.Id, ct);

            var claimed = new List<ExecutorTask>();
            foreach (RunTask task in candidates)
            {
                if (claimed.Count >= max)
                {
                    break;
                }
                if (!runs.TryGetValue(task.RunId, out Run? run)
                    || !evaluations.TryGetValue(run.EvaluationId, out Evaluation? evaluation)
                    || !agents.TryGetValue(evaluation.AgentId, out Agent? agent))
                {
                    continue;
                }
                // El ejecutor genérico no sabe ejecutar "código propio", y tu código sí puede tomar cualquier caso.
                if (mode == "generico" && agent.Kind == AgentKinds.Code)
                {
                    continue;
                }

                task.State = RunTaskState.Claimed;
                task.Worker = worker;
                task.Deliveries++;
                task.LeaseUntil = now.AddSeconds(Math.Clamp(agent.TimeoutSeconds, 1, 1800) + 60);
                // Cada entrega lleva su propia traza: el ejecutor manda traceparent y el agente sus spans.
                task.TraceId = agent.TracesEnabled ? traces.Register() : null;
                claimed.Add(new ExecutorTask(task.Id, task.RunId, task.RowIndex + 1, task.Attempt, task.Input, task.Row, agent, task.TraceId));
            }
            await db.SaveChangesAsync(ct);
            return claimed;
        }
        finally
        {
            _claimLock.Release();
        }
    }

    public async Task<RunResult> CompleteAsync(ExecutorResult input, string worker, CancellationToken ct)
    {
        RunTask task;
        Run run;
        Agent agent;
        await using (EvalDbContext db = await dbFactory.CreateDbContextAsync(ct))
        {
            task = await db.RunTasks.FindAsync([input.TaskId], ct) ?? throw new TaskNotFoundException($"No existe el caso {input.TaskId}.");
            if (task.State == RunTaskState.Done)
            {
                throw new TaskNotFoundException($"El caso {input.TaskId} ya tiene resultado.");
            }
            if (task.State == RunTaskState.Cancelled)
            {
                throw new TaskNotFoundException($"La corrida del caso {input.TaskId} se canceló.");
            }
            run = await db.Runs.SingleAsync(r => r.Id == task.RunId, ct);
            Evaluation evaluation = await db.Evaluations.SingleAsync(e => e.Id == run.EvaluationId, ct);
            agent = await db.Agents.SingleAsync(a => a.Id == evaluation.AgentId, ct);
        }

        List<ToolCallRecord> calls = (input.ToolCalls ?? []).Select(c => new ToolCallRecord
        {
            Name = c.Name,
            Arguments = c.Arguments is JsonElement e && e.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)
                ? AgentInvoker.NormalizeArguments(JsonNode.Parse(e.GetRawText())!)
                : null,
            Result = c.Result,
        }).ToList();

        JsonNode? json = null;
        if (!string.IsNullOrWhiteSpace(input.Raw))
        {
            try
            {
                json = JsonNode.Parse(input.Raw);
            }
            catch (JsonException)
            {
            }
        }

        var invocation = new AgentInvocation
        {
            Output = input.Output ?? "",
            ToolCalls = calls,
            Tools = calls.Select(c => c.Name).ToList(),
            Context = input.Context ?? [],
            Tokens = input.Tokens,
            LatencyMs = input.LatencyMs ?? 0,
            Error = string.IsNullOrWhiteSpace(input.Error) ? null : input.Error,
            RawResponse = input.Raw,
            HttpStatus = input.HttpStatus,
            TraceId = task.TraceId,
            // Sin respuesta cruda, "Comparar otro campo" puede usar output, toolCalls, etc.
            Json = json ?? JsonSerializer.SerializeToNode(new { output = input.Output, toolCalls = calls, context = input.Context, tokens = input.Tokens }, JsonSerializerOptions.Web),
        };

        await TraceEnricher.ApplyAsync(traces, agent, invocation, ct);

        using IServiceScope scope = scopes.CreateScope();
        RunResult result = await scope.ServiceProvider.GetRequiredService<RunExecutor>()
            .GradeAsync(run, agent, task.Row, task.RowIndex, task.Attempt, task.Input, invocation, ct);

        await recorder.RecordAsync(result, ct, db =>
        {
            db.RunTasks.Attach(task);
            task.State = RunTaskState.Done;
            task.Worker = worker;
            return Task.CompletedTask;
        });
        return result;
    }

    public async Task CancelRunAsync(EvalDbContext db, int runId, CancellationToken ct) =>
        await db.RunTasks.Where(t => t.RunId == runId && t.State != RunTaskState.Done)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.State, RunTaskState.Cancelled), ct);

    public async Task<Dictionary<string, (int Pending, int Claimed)>> QueueByGroupAsync(CancellationToken ct)
    {
        await using EvalDbContext db = await dbFactory.CreateDbContextAsync(ct);
        var rows = await db.RunTasks
            .Where(t => t.State == RunTaskState.Pending || t.State == RunTaskState.Claimed)
            .GroupBy(t => new { t.Group, t.State })
            .Select(g => new { g.Key.Group, g.Key.State, Count = g.Count() })
            .ToListAsync(ct);
        return rows.GroupBy(r => r.Group).ToDictionary(
            g => g.Key,
            g => (g.Where(r => r.State == RunTaskState.Pending).Sum(r => r.Count), g.Where(r => r.State == RunTaskState.Claimed).Sum(r => r.Count)));
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9_-]{0,49}$")]
    private static partial Regex GroupName();
}
