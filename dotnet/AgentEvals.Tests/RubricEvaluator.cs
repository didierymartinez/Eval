using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation;

namespace AgentEvals.Tests;

// Juez LLM con la rúbrica de cada fila (equivalente a llm-rubric de promptfoo).
// Usa el IChatClient de ChatConfiguration, así la respuesta del juez queda en caché.
public sealed class RubricEvaluator : IEvaluator
{
    public const string MetricName = "Rúbrica";

    public IReadOnlyCollection<string> EvaluationMetricNames => [MetricName];

    public async ValueTask<EvaluationResult> EvaluateAsync(
        IEnumerable<ChatMessage> messages,
        ChatResponse modelResponse,
        ChatConfiguration? chatConfiguration = null,
        IEnumerable<EvaluationContext>? additionalContext = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chatConfiguration);
        EvalCase evalCase = additionalContext!.OfType<EvalCaseContext>().Single().Case;

        string prompt = $$"""
            Eres un evaluador estricto. Califica la RESPUESTA del asistente contra la RÚBRICA.
            Responde solo JSON: {"puntaje": <entero 1-5>, "razon": "<una frase>"}.
            5 = cumple la rúbrica por completo; 3 = cumple a medias; 1 = no la cumple.

            PREGUNTA DEL USUARIO:
            {{evalCase.Input}}

            RESPUESTA DE REFERENCIA (orientativa, no tiene que coincidir textual):
            {{evalCase.ExpectedOutput}}

            RÚBRICA:
            {{evalCase.Rubric}}

            RESPUESTA DEL ASISTENTE:
            {{modelResponse.Text}}
            """;

        ChatResponse judge = await chatConfiguration.ChatClient.GetResponseAsync(
            [new ChatMessage(ChatRole.User, prompt)],
            new ChatOptions { Temperature = 0, ResponseFormat = ChatResponseFormat.Json },
            cancellationToken);

        NumericMetric metric;
        try
        {
            using JsonDocument json = JsonDocument.Parse(StripCodeFence(judge.Text));
            int score = json.RootElement.GetProperty("puntaje").GetInt32();
            string reason = json.RootElement.GetProperty("razon").GetString() ?? "";
            metric = new NumericMetric(MetricName, value: score, reason: reason);
            bool pass = score >= 4;
            metric.Interpretation = new EvaluationMetricInterpretation(
                score switch { 5 => EvaluationRating.Exceptional, 4 => EvaluationRating.Good, 3 => EvaluationRating.Average, 2 => EvaluationRating.Poor, _ => EvaluationRating.Unacceptable },
                failed: !pass,
                reason: pass ? "Puntaje >= 4." : "Puntaje < 4.");
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            metric = new NumericMetric(MetricName, value: null, reason: $"El juez no devolvió JSON válido: {judge.Text}");
            metric.Interpretation = new EvaluationMetricInterpretation(EvaluationRating.Unknown, failed: true, reason: "Respuesta del juez inválida.");
        }

        return new EvaluationResult(metric);
    }

    private static string StripCodeFence(string text)
    {
        string trimmed = text.Trim();
        if (!trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            return trimmed;
        }

        int start = trimmed.IndexOf('\n') + 1;
        int end = trimmed.LastIndexOf("```", StringComparison.Ordinal);
        return end > start ? trimmed[start..end].Trim() : trimmed;
    }
}
