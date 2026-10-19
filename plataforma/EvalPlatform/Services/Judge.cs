using System.ClientModel;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation;
using Microsoft.Extensions.Options;
using OpenAI;

namespace EvalPlatform.Services;

// Modelo que califica en las comparaciones "(IA)" y "(RAG)". Cualquier API compatible con
// OpenAI: OpenAI, Azure OpenAI (/openai/v1/), Anthropic (/v1/ compatible), Ollama, etc.
public sealed class JudgeOptions
{
    public string? Endpoint { get; set; }
    public string? ApiKey { get; set; }
    public string? Model { get; set; }
}

public interface IJudgeProvider
{
    // null cuando no hay juez configurado.
    ChatConfiguration? Get();
    string? ModelName { get; }
}

public sealed class OpenAiCompatibleJudgeProvider : IJudgeProvider
{
    private readonly ChatConfiguration? _configuration;

    public OpenAiCompatibleJudgeProvider(IOptions<JudgeOptions> options)
    {
        JudgeOptions o = options.Value;
        if (string.IsNullOrWhiteSpace(o.Model) || (string.IsNullOrWhiteSpace(o.ApiKey) && string.IsNullOrWhiteSpace(o.Endpoint)))
        {
            return;
        }

        var clientOptions = new OpenAIClientOptions();
        if (!string.IsNullOrWhiteSpace(o.Endpoint))
        {
            clientOptions.Endpoint = new Uri(o.Endpoint);
        }

        // Algunos servidores locales no piden clave, pero el cliente exige una.
        var credential = new ApiKeyCredential(string.IsNullOrWhiteSpace(o.ApiKey) ? "sin-clave" : o.ApiKey);
        IChatClient client = new OpenAIClient(credential, clientOptions).GetChatClient(o.Model).AsIChatClient();
        _configuration = new ChatConfiguration(client);
        ModelName = o.Model;
    }

    public string? ModelName { get; }

    public ChatConfiguration? Get() => _configuration;
}

// Juez con criterio libre (rúbrica) o de cobertura, con el mismo formato de salida que los
// evaluadores de Microsoft.Extensions.AI.Evaluation: <S0>razonamiento</S0><S1>explicación</S1><S2>puntaje</S2>.
public static partial class CustomJudges
{
    // Como "Correctness" de n8n, pero con explicación: qué falta o qué contradice.
    public static Task<(double? Score, string Reason)> CorrectnessAsync(ChatConfiguration judge, string question, string answer, string expected, CancellationToken ct) =>
        AskAsync(judge, $$"""
            Eres un evaluador estricto. Compara el SIGNIFICADO de la RESPUESTA con la RESPUESTA ESPERADA.
            No importa la redacción ni el largo; importa que tenga los mismos datos y pasos, y que no contradiga nada.
            5 = equivalente: tiene todos los datos clave y no contradice;
            4 = casi equivalente: falta un detalle menor;
            3 = parcialmente correcta: falta información importante;
            2 = mayormente incorrecta o incompleta;
            1 = incorrecta, contradice lo esperado o no responde.
            Escribe la explicación en español y nombra los datos que faltan o que están mal, si los hay.

            PREGUNTA O ENTRADA:
            {{question}}

            RESPUESTA ESPERADA:
            {{expected}}

            RESPUESTA:
            {{answer}}

            Da tus respuestas entre las etiquetas: <S0>tu razonamiento</S0>, <S1>tu explicación</S1>, <S2>tu puntaje (solo el número)</S2>.
            """, ct);

    public static Task<(double? Score, string Reason)> RubricAsync(ChatConfiguration judge, string question, string answer, string criterion, CancellationToken ct) =>
        AskAsync(judge, $$"""
            Eres un evaluador estricto. Califica de 1 a 5 qué tanto cumple la RESPUESTA el CRITERIO.
            5 = lo cumple por completo; 4 = lo cumple con detalles menores; 3 = a medias; 2 = casi no; 1 = no lo cumple.
            Escribe la explicación en español, en una o dos frases.

            PREGUNTA O ENTRADA:
            {{question}}

            CRITERIO:
            {{criterion}}

            RESPUESTA:
            {{answer}}

            Da tus respuestas entre las etiquetas: <S0>tu razonamiento</S0>, <S1>tu explicación</S1>, <S2>tu puntaje (solo el número)</S2>.
            """, ct);

    public static Task<(double? Score, string Reason)> CoverageAsync(ChatConfiguration judge, string question, string expected, IReadOnlyList<string> context, CancellationToken ct) =>
        AskAsync(judge, $$"""
            Eres un evaluador estricto de sistemas RAG. Decide si los DOCUMENTOS RECUPERADOS contienen la información
            necesaria para dar la RESPUESTA ESPERADA a la pregunta. No juzgues la redacción: solo si los datos están.
            5 = todos los datos clave de la respuesta esperada están en los documentos;
            4 = casi todos, falta un detalle menor;
            3 = la mitad;
            2 = muy pocos;
            1 = ninguno, o los documentos no tienen relación.
            Escribe la explicación en español y nombra los datos que faltan, si faltan.

            PREGUNTA:
            {{question}}

            RESPUESTA ESPERADA:
            {{expected}}

            DOCUMENTOS RECUPERADOS:
            {{string.Join("\n---\n", context)}}

            Da tus respuestas entre las etiquetas: <S0>tu razonamiento</S0>, <S1>tu explicación</S1>, <S2>tu puntaje (solo el número)</S2>.
            """, ct);

    private static async Task<(double? Score, string Reason)> AskAsync(ChatConfiguration judge, string prompt, CancellationToken ct)
    {
        ChatResponse response = await judge.ChatClient.GetResponseAsync(
            [new ChatMessage(ChatRole.User, prompt)],
            new ChatOptions { Temperature = 0 },
            ct);
        string text = response.Text;
        string reason = Tag(text, 1) ?? text.Trim();
        return double.TryParse(Tag(text, 2)?.Trim(), System.Globalization.CultureInfo.InvariantCulture, out double score) && score is >= 1 and <= 5
            ? (score, reason)
            : (null, $"El juez no devolvió un puntaje válido: {Truncate(text, 300)}");
    }

    private static string? Tag(string text, int n)
    {
        Match m = Regex.Match(text, $@"<S{n}>(.*?)</S{n}>", RegexOptions.Singleline);
        return m.Success ? m.Groups[1].Value.Trim() : null;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
