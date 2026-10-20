using System.Collections.Concurrent;
using System.Threading.Channels;
using EvalPlatform.Data;
using EvalPlatform.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace EvalPlatform.Services;

public sealed class RunnerOptions
{
    // Casos en paralelo por corrida. Bájalo si el agente se satura.
    public int MaxConcurrency { get; set; } = 3;
}

// Cola de corridas pendientes y cancelación de las que están en curso.
public sealed class RunQueue
{
    private readonly Channel<int> _channel = Channel.CreateUnbounded<int>();
    private readonly ConcurrentDictionary<int, CancellationTokenSource> _running = new();

    public ValueTask EnqueueAsync(int runId) => _channel.Writer.WriteAsync(runId);

    public IAsyncEnumerable<int> ReadAllAsync(CancellationToken ct) => _channel.Reader.ReadAllAsync(ct);

    public CancellationTokenSource Register(int runId, CancellationToken stopping)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        _running[runId] = cts;
        return cts;
    }

    public void Unregister(int runId)
    {
        if (_running.TryRemove(runId, out CancellationTokenSource? cts))
        {
            cts.Dispose();
        }
    }

    public bool Cancel(int runId)
    {
        if (_running.TryGetValue(runId, out CancellationTokenSource? cts))
        {
            cts.Cancel();
            return true;
        }
        return false;
    }
}

// Guarda el resultado de un caso y recalcula los contadores de la corrida. Lo usan la plataforma
// y los ejecutores remotos; los contadores se cuentan en la base para que varios ejecutores
// en paralelo no se pisen.
public sealed class RunRecorder(IDbContextFactory<EvalDbContext> dbFactory)
{
    private readonly SemaphoreSlim _lock = new(1, 1);

    public async Task RecordAsync(RunResult result, CancellationToken ct, Func<EvalDbContext, Task>? alsoInTransaction = null)
    {
        await _lock.WaitAsync(ct);
        try
        {
            await using EvalDbContext db = await dbFactory.CreateDbContextAsync(ct);
            db.RunResults.Add(result);
            if (alsoInTransaction is not null)
            {
                await alsoInTransaction(db);
            }
            await db.SaveChangesAsync(ct);

            var counts = await db.RunResults.Where(r => r.RunId == result.RunId)
                .GroupBy(r => r.Outcome)
                .Select(g => new { g.Key, Count = g.Count() })
                .ToListAsync(ct);
            int Count(CaseOutcome o) => counts.FirstOrDefault(c => c.Key == o)?.Count ?? 0;
            int completed = counts.Sum(c => c.Count);

            Run run = await db.Runs.SingleAsync(r => r.Id == result.RunId, ct);
            run.Completed = completed;
            run.Passed = Count(CaseOutcome.Passed);
            run.Failed = Count(CaseOutcome.Failed);
            run.Errors = Count(CaseOutcome.Error);
            // Las corridas remotas terminan cuando llega el último resultado.
            if (run.Executor is not null && run.Status == RunStatus.Running && completed >= run.Total)
            {
                run.Status = RunStatus.Completed;
                run.FinishedAt = DateTimeOffset.UtcNow;
            }
            await db.SaveChangesAsync(ct);
        }
        finally
        {
            _lock.Release();
        }
    }
}

// Corre una evaluación: cada caso (y cada repetición) va al agente y pasa por las comparaciones.
// Si el agente lo llama un ejecutor remoto, solo deja los casos listos para que los tomen.
public sealed class RunExecutor(
    IDbContextFactory<EvalDbContext> dbFactory,
    AgentDispatcher invoker,
    CheckRunner checks,
    RunRecorder recorder,
    IOptions<RunnerOptions> options,
    ILogger<RunExecutor> logger)
{
    public async Task ExecuteAsync(int runId, CancellationToken cancellationToken)
    {
        Run run;
        Evaluation evaluation;
        Agent agent;
        Dataset dataset;
        await using (EvalDbContext db = await dbFactory.CreateDbContextAsync(cancellationToken))
        {
            run = await db.Runs.SingleAsync(r => r.Id == runId, cancellationToken);
            if (run.Status != RunStatus.Queued)
            {
                return;
            }
            Evaluation? e = await db.Evaluations.FindAsync([run.EvaluationId], cancellationToken);
            Agent? a = e is null ? null : await db.Agents.FindAsync([e.AgentId], cancellationToken);
            Dataset? d = e is null ? null : await db.Datasets.FindAsync([e.DatasetId], cancellationToken);
            if (e is null || a is null || d is null)
            {
                run.Status = RunStatus.Failed;
                run.Error = "La evaluación, el agente o el dataset ya no existen.";
                run.FinishedAt = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync(cancellationToken);
                return;
            }
            (evaluation, agent, dataset) = (e, a, d);

            run.Status = RunStatus.Running;
            run.StartedAt = DateTimeOffset.UtcNow;
            run.Total = dataset.Rows.Count * Math.Max(1, evaluation.Repetitions);

            if (agent.RunsRemotely)
            {
                run.Executor = RemoteTasks.GroupOf(agent);
                db.RunTasks.AddRange(Cases(dataset, evaluation).Select(c => new RunTask
                {
                    RunId = run.Id,
                    Group = run.Executor,
                    RowIndex = c.Index,
                    Attempt = c.Attempt,
                    Row = dataset.Rows[c.Index],
                    Input = InputOf(evaluation, dataset.Rows[c.Index]),
                }));
                await db.SaveChangesAsync(cancellationToken);
                return;
            }
            await db.SaveChangesAsync(cancellationToken);
        }

        RunStatus final = RunStatus.Completed;
        string? finalError = null;
        try
        {
            await Parallel.ForEachAsync(
                Cases(dataset, evaluation),
                new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, options.Value.MaxConcurrency), CancellationToken = cancellationToken },
                async (c, ct) =>
                {
                    Dictionary<string, string> row = dataset.Rows[c.Index];
                    string input = InputOf(evaluation, row);
                    AgentInvocation invocation = await invoker.InvokeAsync(agent, input, row, ct);
                    RunResult result = await GradeAsync(run, agent, row, c.Index, c.Attempt, input, invocation, ct);
                    await recorder.RecordAsync(result, ct);
                });
        }
        catch (OperationCanceledException)
        {
            final = RunStatus.Cancelled;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Falló la corrida {RunId}", runId);
            final = RunStatus.Failed;
            finalError = ex.Message;
        }

        await using (EvalDbContext db = await dbFactory.CreateDbContextAsync(CancellationToken.None))
        {
            await db.Runs.Where(r => r.Id == runId).ExecuteUpdateAsync(s => s
                .SetProperty(r => r.Status, final)
                .SetProperty(r => r.Error, finalError)
                .SetProperty(r => r.FinishedAt, DateTimeOffset.UtcNow));
        }
    }

    private static IEnumerable<(int Index, int Attempt)> Cases(Dataset dataset, Evaluation evaluation) =>
        Enumerable.Range(0, dataset.Rows.Count)
            .SelectMany(i => Enumerable.Range(1, Math.Max(1, evaluation.Repetitions)).Select(attempt => (i, attempt)));

    public static string InputOf(Evaluation evaluation, Dictionary<string, string> row) =>
        row.TryGetValue(evaluation.InputColumn, out string? value) ? value : "";

    // Aplica las comparaciones de la corrida a la respuesta del agente (venga de donde venga).
    public async Task<RunResult> GradeAsync(Run run, Agent agent, Dictionary<string, string> row, int index, int attempt, string input, AgentInvocation invocation, CancellationToken ct)
    {
        var result = new RunResult
        {
            RunId = run.Id,
            RowIndex = index,
            Attempt = attempt,
            Row = row,
            Input = input,
            Output = invocation.Output,
            Context = invocation.Context,
            Tools = invocation.Tools,
            ToolCalls = invocation.ToolCalls,
            RawResponse = invocation.RawResponse,
            HttpStatus = invocation.HttpStatus,
            LatencyMs = invocation.LatencyMs,
            Tokens = invocation.Tokens,
        };

        if (!invocation.Succeeded)
        {
            result.Outcome = CaseOutcome.Error;
            result.Error = invocation.Error;
            return result;
        }

        var data = new CaseData(agent, input, row, invocation);
        foreach (CheckDefinition check in run.Checks)
        {
            result.Checks.Add(await checks.RunAsync(check, data, ct));
        }

        List<CheckResult> blocking = result.Checks.Where(c => c.Blocking && c.Status != CheckStatus.Skipped).ToList();
        result.Outcome = blocking.Any(c => c.Status == CheckStatus.Error) ? CaseOutcome.Error
            : blocking.Any(c => c.Status == CheckStatus.Failed) ? CaseOutcome.Failed
            : CaseOutcome.Passed;
        if (result.Outcome == CaseOutcome.Error)
        {
            result.Error = string.Join(" ", blocking.Where(c => c.Status == CheckStatus.Error).Select(c => $"{c.Label}: {c.Reason}"));
        }
        return result;
    }
}

public sealed class RunWorker(RunQueue queue, IServiceScopeFactory scopes, IDbContextFactory<EvalDbContext> dbFactory, ILogger<RunWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Corridas que quedaron a medias por un reinicio: las que ejecutaba la plataforma se marcan
        // fallidas y las que esperaban vuelven a la cola. Las remotas siguen: sus casos están en la base.
        await using (EvalDbContext db = await dbFactory.CreateDbContextAsync(stoppingToken))
        {
            await db.Runs.Where(r => r.Status == RunStatus.Running && r.Executor == null).ExecuteUpdateAsync(s => s
                .SetProperty(r => r.Status, RunStatus.Failed)
                .SetProperty(r => r.Error, "Interrumpida porque la plataforma se reinició.")
                .SetProperty(r => r.FinishedAt, DateTimeOffset.UtcNow), stoppingToken);
            foreach (int id in await db.Runs.Where(r => r.Status == RunStatus.Queued).OrderBy(r => r.Id).Select(r => r.Id).ToListAsync(stoppingToken))
            {
                await queue.EnqueueAsync(id);
            }
        }

        await foreach (int runId in queue.ReadAllAsync(stoppingToken))
        {
            CancellationTokenSource cts = queue.Register(runId, stoppingToken);
            try
            {
                using IServiceScope scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<RunExecutor>().ExecuteAsync(runId, cts.Token);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(ex, "No se pudo ejecutar la corrida {RunId}", runId);
            }
            finally
            {
                queue.Unregister(runId);
            }
        }
    }
}
