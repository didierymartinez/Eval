using System.Collections.Concurrent;
using System.Threading.Channels;
using EvalPlatform.Data;
using EvalPlatform.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace EvalPlatform.Services;

public sealed class RunnerOptions
{
    // Casos en paralelo por corrida. Bájalo si el agente (o n8n) se satura.
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

// Corre una evaluación: cada caso (y cada repetición) va al agente y pasa por las comparaciones.
public sealed class RunExecutor(
    IDbContextFactory<EvalDbContext> dbFactory,
    AgentDispatcher invoker,
    CheckRunner checks,
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
            await db.SaveChangesAsync(cancellationToken);
        }

        var cases = Enumerable.Range(0, dataset.Rows.Count)
            .SelectMany(i => Enumerable.Range(1, Math.Max(1, evaluation.Repetitions)).Select(attempt => (Index: i, Attempt: attempt)))
            .ToList();

        int completed = 0, passed = 0, failed = 0, errors = 0;
        var writeLock = new SemaphoreSlim(1, 1);
        RunStatus final = RunStatus.Completed;
        string? finalError = null;

        try
        {
            await Parallel.ForEachAsync(
                cases,
                new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, options.Value.MaxConcurrency), CancellationToken = cancellationToken },
                async (c, ct) =>
                {
                    RunResult result = await EvaluateCaseAsync(run, agent, evaluation, dataset.Rows[c.Index], c.Index, c.Attempt, ct);

                    await writeLock.WaitAsync(ct);
                    try
                    {
                        completed++;
                        switch (result.Outcome)
                        {
                            case CaseOutcome.Passed: passed++; break;
                            case CaseOutcome.Failed: failed++; break;
                            default: errors++; break;
                        }

                        await using EvalDbContext db = await dbFactory.CreateDbContextAsync(ct);
                        db.RunResults.Add(result);
                        await db.SaveChangesAsync(ct);
                        await db.Runs.Where(r => r.Id == runId).ExecuteUpdateAsync(s => s
                            .SetProperty(r => r.Completed, completed)
                            .SetProperty(r => r.Passed, passed)
                            .SetProperty(r => r.Failed, failed)
                            .SetProperty(r => r.Errors, errors), ct);
                    }
                    finally
                    {
                        writeLock.Release();
                    }
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

    public async Task<RunResult> EvaluateCaseAsync(Run run, Agent agent, Evaluation evaluation, Dictionary<string, string> row, int index, int attempt, CancellationToken ct)
    {
        string input = row.TryGetValue(evaluation.InputColumn, out string? value) ? value : "";
        AgentInvocation invocation = await invoker.InvokeAsync(agent, input, row, ct);

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
        // Corridas que quedaron a medias por un reinicio: las en curso se marcan fallidas
        // y las que esperaban vuelven a la cola.
        await using (EvalDbContext db = await dbFactory.CreateDbContextAsync(stoppingToken))
        {
            await db.Runs.Where(r => r.Status == RunStatus.Running).ExecuteUpdateAsync(s => s
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
