using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation;

namespace AgentEvals.Tests;

// Lleva la fila del dataset hasta los evaluadores.
public sealed class EvalCaseContext(EvalCase evalCase) : EvaluationContext("Caso", evalCase.Id)
{
    public EvalCase Case { get; } = evalCase;
}

// Mismas reglas que promptfoo/assertions.js: todos los must_contain y ninguno de
// los must_not_contain (sin distinguir mayúsculas).
public sealed class ContentRulesEvaluator : IEvaluator
{
    public const string MetricName = "Contenido";

    public IReadOnlyCollection<string> EvaluationMetricNames => [MetricName];

    public ValueTask<EvaluationResult> EvaluateAsync(
        IEnumerable<ChatMessage> messages,
        ChatResponse modelResponse,
        ChatConfiguration? chatConfiguration = null,
        IEnumerable<EvaluationContext>? additionalContext = null,
        CancellationToken cancellationToken = default)
    {
        EvalCase evalCase = additionalContext!.OfType<EvalCaseContext>().Single().Case;
        string text = modelResponse.Text;

        var problems = new List<string>();
        problems.AddRange(EvalCase.SplitList(evalCase.MustContain)
            .Where(v => !text.Contains(v, StringComparison.OrdinalIgnoreCase))
            .Select(v => $"falta \"{v}\""));
        problems.AddRange(EvalCase.SplitList(evalCase.MustNotContain)
            .Where(v => text.Contains(v, StringComparison.OrdinalIgnoreCase))
            .Select(v => $"contiene prohibido \"{v}\""));

        var metric = new BooleanMetric(MetricName, value: problems.Count == 0,
            reason: problems.Count == 0 ? "Cumple must_contain / must_not_contain." : string.Join("; ", problems));
        metric.Interpretation = Interpret(metric.Value == true, metric.Reason!);
        return new ValueTask<EvaluationResult>(new EvaluationResult(metric));
    }

    internal static EvaluationMetricInterpretation Interpret(bool pass, string reason) =>
        new(pass ? EvaluationRating.Good : EvaluationRating.Unacceptable, failed: !pass, reason: reason);
}

// Revisa que se llamaron las herramientas de expected_tools. El orden no importa y se
// permiten llamadas extra (se califica el resultado, no un camino rígido).
// "ninguna" = no debe llamar herramientas; vacío = no se revisa.
public sealed class ExpectedToolsEvaluator : IEvaluator
{
    public const string MetricName = "Herramientas";

    public IReadOnlyCollection<string> EvaluationMetricNames => [MetricName];

    public ValueTask<EvaluationResult> EvaluateAsync(
        IEnumerable<ChatMessage> messages,
        ChatResponse modelResponse,
        ChatConfiguration? chatConfiguration = null,
        IEnumerable<EvaluationContext>? additionalContext = null,
        CancellationToken cancellationToken = default)
    {
        EvalCase evalCase = additionalContext!.OfType<EvalCaseContext>().Single().Case;
        IReadOnlyList<string> expected = EvalCase.SplitList(evalCase.ExpectedTools);
        List<string> called = modelResponse.Messages
            .SelectMany(m => m.Contents)
            .OfType<FunctionCallContent>()
            .Select(c => c.Name)
            .ToList();
        string calledText = called.Count == 0 ? "nada" : string.Join(", ", called);

        BooleanMetric metric;
        if (expected.Count == 0)
        {
            metric = new BooleanMetric(MetricName, value: true, reason: $"Sin expectativa de herramientas (llamó: {calledText}).");
        }
        else if (expected.Count == 1 && expected[0].Equals("ninguna", StringComparison.OrdinalIgnoreCase))
        {
            metric = new BooleanMetric(MetricName, value: called.Count == 0, reason: $"Se esperaba no llamar herramientas (llamó: {calledText}).");
        }
        else
        {
            List<string> missing = expected.Except(called).ToList();
            metric = new BooleanMetric(MetricName, value: missing.Count == 0,
                reason: missing.Count == 0
                    ? $"Llamó {string.Join(", ", expected)}."
                    : $"Faltó llamar: {string.Join(", ", missing)} (llamó: {calledText}).");
        }

        metric.Interpretation = ContentRulesEvaluator.Interpret(metric.Value == true, metric.Reason!);
        return new ValueTask<EvaluationResult>(new EvaluationResult(metric));
    }
}
