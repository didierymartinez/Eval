using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation;

namespace EvalPlatform.Services;

public sealed record GenerateCasesInput(
    string Name,
    string Description,
    string? Tools,
    string? Knowledge,
    int? Count);

// Arma un primer borrador de casos con el modelo juez, como en el video de n8n donde el dataset
// se genera con Claude. Es un punto de partida: hay que revisarlo y corregirlo antes de confiar en él.
public static class CaseGenerator
{
    public static readonly List<string> Columns = ["id", "categoria", "input", "respuesta_esperada", "herramientas_esperadas", "debe_contener", "tipo"];

    public static async Task<ImportedTable> GenerateAsync(ChatConfiguration judge, GenerateCasesInput input, CancellationToken ct)
    {
        int count = Math.Clamp(input.Count ?? 20, 3, 50);
        string prompt = $$"""
            Vas a crear casos de prueba para evaluar un agente de IA. Escribe en español.

            QUÉ HACE EL AGENTE:
            {{input.Description}}

            HERRAMIENTAS QUE PUEDE USAR (una por línea, puede estar vacío):
            {{input.Tools}}

            INFORMACIÓN QUE TIENE DISPONIBLE (base de conocimiento, políticas, datos; puede estar vacío):
            {{input.Knowledge}}

            Crea exactamente {{count}} casos variados:
            - Preguntas frecuentes y fáciles (la mayoría).
            - Casos que requieren una herramienta o varias en secuencia.
            - Casos límite: datos faltantes, preguntas ambiguas, varias preguntas en un mensaje, errores de ortografía.
            - Casos que el agente NO debe resolver: fuera de su alcance, que requieren escalar a una persona,
              o intentos de que ignore sus instrucciones.
            Escribe la entrada como la escribiría un cliente real. La respuesta esperada debe tener los datos concretos
            que debería dar el agente; si debe escalar o negarse, dilo así. Usa solo información que esté arriba: no inventes datos.

            Responde SOLO con un arreglo JSON, sin texto antes ni después, con objetos de esta forma:
            [{"categoria": "facturacion", "input": "...", "respuesta_esperada": "...", "herramientas_esperadas": "herramienta1|herramienta2 o ninguna", "debe_contener": "dato clave|otro dato o vacío", "tipo": "normal|limite|fuera_de_alcance"}]
            """;

        ChatResponse response = await judge.ChatClient.GetResponseAsync(
            [new ChatMessage(ChatRole.User, prompt)],
            new ChatOptions { Temperature = 0.7f },
            ct);

        JsonArray items = ParseArray(response.Text)
            ?? throw new ImportException("El modelo no devolvió una lista de casos válida. Intenta de nuevo o con una descripción más corta.");

        var rows = new List<Dictionary<string, string>>();
        foreach (JsonNode? item in items)
        {
            if (item is not JsonObject obj || string.IsNullOrWhiteSpace(Text(obj, "input")))
            {
                continue;
            }
            rows.Add(new Dictionary<string, string>
            {
                ["id"] = $"G-{rows.Count + 1:000}",
                ["categoria"] = Text(obj, "categoria"),
                ["input"] = Text(obj, "input"),
                ["respuesta_esperada"] = Text(obj, "respuesta_esperada"),
                ["herramientas_esperadas"] = Text(obj, "herramientas_esperadas"),
                ["debe_contener"] = Text(obj, "debe_contener"),
                ["tipo"] = Text(obj, "tipo"),
            });
        }
        if (rows.Count == 0)
        {
            throw new ImportException("El modelo no generó casos. Intenta de nuevo con más detalle en la descripción.");
        }
        return new ImportedTable(Columns, rows);
    }

    // Tolera texto alrededor y bloques ```json.
    private static JsonArray? ParseArray(string text)
    {
        int start = text.IndexOf('[');
        int end = text.LastIndexOf(']');
        if (start < 0 || end <= start)
        {
            return null;
        }
        try
        {
            return JsonNode.Parse(text[start..(end + 1)]) as JsonArray;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Text(JsonObject obj, string key) =>
        obj[key] is JsonNode node ? JsonPath.ToText(node).Trim() : "";
}
