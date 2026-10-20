using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace EvalPlatform.Services;

// Rutas simples para sacar datos de la respuesta del agente, pensadas para escribirse a mano:
//   output                         -> la propiedad "output"
//   output.respuesta               -> propiedades anidadas
//   choices[0].message.content     -> índice de una lista
//   intermediateSteps[*].observation -> todos los elementos de una lista
// Dos comodidades para respuestas de orquestadores de agentes:
//   - Si se pide una propiedad sobre una lista, se aplica a cada elemento.
//   - Si un valor es texto que contiene JSON, se interpreta como JSON al seguir navegando.
public static partial class JsonPath
{
    public static IReadOnlyList<JsonNode?> Select(JsonNode? root, string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return [root];
        }

        List<JsonNode?> current = [root];
        foreach (Match token in Token().Matches(path))
        {
            var next = new List<JsonNode?>();
            foreach (JsonNode? node in current)
            {
                if (token.Groups["name"].Success)
                {
                    SelectProperty(ParseIfJsonText(node), token.Groups["name"].Value, next);
                }
                else if (ParseIfJsonText(node) is JsonArray array)
                {
                    string index = token.Groups["index"].Value;
                    if (index == "*")
                    {
                        next.AddRange(array);
                    }
                    else if (int.TryParse(index, out int i) && i >= 0 && i < array.Count)
                    {
                        next.Add(array[i]);
                    }
                }
            }
            current = next;
        }
        return current;
    }

    // Texto de un valor: los textos tal cual, lo demás como JSON.
    public static string ToText(JsonNode? node) => node switch
    {
        null => "",
        JsonValue value when value.TryGetValue(out string? s) => s,
        JsonValue value => value.ToJsonString(),
        _ => node.ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }),
    };

    public static string SelectText(JsonNode? root, string? path) =>
        string.Join("\n", Select(root, path).Select(ToText).Where(t => t.Length > 0));

    // Aplana fragmentos de documentos que vienen con formas distintas según la herramienta:
    // texto, JSON en texto, [{ pageContent }], [{ type: "text", text: "..." }], etc.
    public static List<string> SelectTexts(JsonNode? root, string? path)
    {
        var texts = new List<string>();
        foreach (JsonNode? node in Select(root, path))
        {
            CollectTexts(node, texts, depth: 0);
        }
        return texts;
    }

    private static void CollectTexts(JsonNode? node, List<string> texts, int depth)
    {
        if (node is null || depth > 8)
        {
            return;
        }

        JsonNode? parsed = ParseIfJsonText(node);
        if (!ReferenceEquals(parsed, node))
        {
            CollectTexts(parsed, texts, depth + 1);
            return;
        }

        switch (node)
        {
            case JsonArray array:
                foreach (JsonNode? item in array)
                {
                    CollectTexts(item, texts, depth + 1);
                }
                break;
            case JsonObject obj:
                JsonNode? inner = obj["pageContent"] ?? obj["text"] ?? obj["content"];
                if (inner is not null)
                {
                    CollectTexts(inner, texts, depth + 1);
                }
                else
                {
                    texts.Add(ToText(obj));
                }
                break;
            default:
                string text = ToText(node).Trim();
                if (text.Length > 0)
                {
                    texts.Add(text);
                }
                break;
        }
    }

    private static void SelectProperty(JsonNode? node, string name, List<JsonNode?> into)
    {
        switch (node)
        {
            case JsonObject obj when obj.TryGetPropertyValue(name, out JsonNode? value):
                into.Add(value);
                break;
            case JsonArray array:
                foreach (JsonNode? item in array)
                {
                    SelectProperty(ParseIfJsonText(item), name, into);
                }
                break;
        }
    }

    private static JsonNode? ParseIfJsonText(JsonNode? node)
    {
        if (node is JsonValue value && value.TryGetValue(out string? text))
        {
            string trimmed = text.TrimStart();
            if (trimmed.StartsWith('{') || trimmed.StartsWith('['))
            {
                try
                {
                    return JsonNode.Parse(text);
                }
                catch (JsonException)
                {
                }
            }
        }
        return node;
    }

    [GeneratedRegex(@"(?<name>[^.\[\]]+)|\[(?<index>\*|\d+)\]")]
    private static partial Regex Token();
}
