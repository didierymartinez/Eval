using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace EvalPlatform.Services;

// Reemplaza {{columna}}, {{input}} y {{env:VARIABLE}} en URL, encabezados y cuerpo.
public static partial class Templates
{
    public static string RenderText(string template, IReadOnlyDictionary<string, string> vars, Func<string, string>? escape = null) =>
        Placeholder().Replace(template, m =>
        {
            string value = Resolve(m, vars);
            return escape is null ? value : escape(value);
        });

    // El cuerpo es JSON con marcadores dentro de los textos ("{{input}}"). Se reemplaza
    // valor por valor para que comillas o saltos de línea del caso no rompan el JSON.
    // Si la plantilla no es JSON válido se reemplaza como texto plano.
    public static string RenderBody(string template, IReadOnlyDictionary<string, string> vars)
    {
        if (string.IsNullOrWhiteSpace(template))
        {
            return "";
        }

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(template);
        }
        catch (JsonException)
        {
            return RenderText(template, vars);
        }

        return Render(root, vars)?.ToJsonString() ?? "null";
    }

    public static IEnumerable<string> Placeholders(string template) =>
        Placeholder().Matches(template).Select(m => m.Groups["name"].Value.Trim());

    private static JsonNode? Render(JsonNode? node, IReadOnlyDictionary<string, string> vars)
    {
        switch (node)
        {
            case JsonObject obj:
                var newObj = new JsonObject();
                foreach (var (key, value) in obj)
                {
                    newObj[RenderText(key, vars)] = Render(value, vars);
                }
                return newObj;
            case JsonArray array:
                var newArray = new JsonArray();
                foreach (JsonNode? item in array)
                {
                    newArray.Add(Render(item, vars));
                }
                return newArray;
            case JsonValue value when value.TryGetValue(out string? text):
                return JsonValue.Create(RenderText(text, vars));
            default:
                return node?.DeepClone();
        }
    }

    private static string Resolve(Match match, IReadOnlyDictionary<string, string> vars)
    {
        string name = match.Groups["name"].Value.Trim();
        if (match.Groups["env"].Success)
        {
            return Environment.GetEnvironmentVariable(name) ?? "";
        }
        return vars.TryGetValue(name, out string? value) ? value : "";
    }

    [GeneratedRegex(@"\{\{\s*(?<env>env:)?(?<name>[^{}]+?)\s*\}\}")]
    private static partial Regex Placeholder();
}
