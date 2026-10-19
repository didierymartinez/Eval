using System.ClientModel;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation;
using Microsoft.Extensions.AI.Evaluation.Quality;
using Microsoft.Extensions.AI.Evaluation.Reporting;
using Microsoft.Extensions.AI.Evaluation.Reporting.Storage;
using OpenAI;

namespace AgentEvals.Tests;

// Un test por fila del dataset: llama al agente por URL, corre los evaluadores y
// guarda los resultados para el reporte HTML (dotnet aieval report).
//
// Variables de entorno:
//   AGENT_URL, AGENT_EVAL_KEY       endpoint del agente (por defecto el de samples/AgentEndpoint)
//   JUDGE_API_KEY, JUDGE_MODEL      activan los evaluadores LLM (rúbrica, equivalencia, relevancia)
//   JUDGE_ENDPOINT                  base URL compatible con OpenAI (OpenAI, Azure OpenAI v1, etc.)
//   EVAL_RESULTS_PATH               dónde se guardan resultados y caché
//   EVAL_EXECUTION_NAME             nombre de la corrida (p. ej. el número de build de CI)
public sealed class AgentEvalTests
{
    private static readonly ChatConfiguration? s_judge = CreateJudge();

    private static readonly ReportingConfiguration s_reporting = DiskBasedReportingConfiguration.Create(
        storageRootPath: Environment.GetEnvironmentVariable("EVAL_RESULTS_PATH")
            ?? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "eval-results")),
        evaluators: GetEvaluators(),
        chatConfiguration: s_judge,
        enableResponseCaching: s_judge is not null,
        executionName: Environment.GetEnvironmentVariable("EVAL_EXECUTION_NAME") ?? $"{DateTime.Now:yyyyMMddTHHmmss}");

    private readonly AgentClient _agent = new();

    public static TheoryData<string> Cases => EvalDataset.Ids;

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Caso(string id)
    {
        EvalCase evalCase = EvalDataset.Get(id);

        await using ScenarioRun scenarioRun = await s_reporting.CreateScenarioRunAsync(
            scenarioName: $"AgentePedidos.{evalCase.Category}.{evalCase.Id}",
            additionalTags: [evalCase.Category]);

        var (messages, response, _) = await _agent.RunAsync(evalCase);

        EvaluationResult result = await scenarioRun.EvaluateAsync(
            messages,
            response,
            additionalContext: [new EvalCaseContext(evalCase), new EquivalenceEvaluatorContext(evalCase.ExpectedOutput)]);

        List<string> failures = result.Metrics.Values
            .Where(m => m.Interpretation?.Failed == true)
            .Select(m => $"{m.Name}: {m.Reason}")
            .ToList();

        Assert.True(failures.Count == 0, $"[{evalCase.Id}] {string.Join(" | ", failures)}\nRespuesta: {response.Text}");
    }

    private static IEnumerable<IEvaluator> GetEvaluators()
    {
        yield return new ContentRulesEvaluator();
        yield return new ExpectedToolsEvaluator();

        if (s_judge is not null)
        {
            yield return new RubricEvaluator();
            yield return new EquivalenceEvaluator();
            yield return new RelevanceEvaluator();
        }
    }

    private static ChatConfiguration? CreateJudge()
    {
        string? apiKey = Environment.GetEnvironmentVariable("JUDGE_API_KEY");
        string? model = Environment.GetEnvironmentVariable("JUDGE_MODEL");
        if (string.IsNullOrEmpty(apiKey) || string.IsNullOrEmpty(model))
        {
            return null;
        }

        var options = new OpenAIClientOptions();
        if (Environment.GetEnvironmentVariable("JUDGE_ENDPOINT") is { Length: > 0 } endpoint)
        {
            options.Endpoint = new Uri(endpoint);
        }

        IChatClient client = new OpenAIClient(new ApiKeyCredential(apiKey), options).GetChatClient(model).AsIChatClient();
        return new ChatConfiguration(client);
    }
}
