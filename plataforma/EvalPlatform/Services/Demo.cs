using System.Text.Json.Nodes;
using EvalPlatform.Data;
using EvalPlatform.Domain;
using Microsoft.EntityFrameworkCore;

namespace EvalPlatform.Services;

// Agente de mentira que responde como un workflow de n8n con AI Agent + vector store,
// para probar la plataforma sin conectar nada. La base de conocimiento es mínima y
// la búsqueda es por palabras. Le falta a propósito cómo crear usuarios (HD-005) y
// tiene datos de la empresa que lo hacen responder un caso que debía escalar (HD-006).
public static class DemoHelpdeskAgent
{
    private static readonly (string Source, string Text)[] s_knowledge =
    [
        ("manual-acceso.pdf", "Después de 5 intentos fallidos el usuario se bloquea durante 30 minutos. Para desbloquearlo de inmediato use la opción Olvidé mi contraseña en la pantalla de ingreso."),
        ("manual-facturacion.pdf", "Las facturas electrónicas enviadas a la DIAN no se pueden anular. Para corregirlas emita una nota crédito desde Ventas > Notas crédito referenciando la factura original."),
        ("manual-inventario.pdf", "Los reportes de inventario se generan en Inventario > Reportes. Una vez generado, use el botón Exportar y elija Excel o PDF."),
        ("manual-nomina.pdf", "Los recargos se configuran en Nómina > Parámetros > Recargos. La hora extra nocturna tiene por defecto un recargo de 75%."),
        ("manual-empresa.pdf", "Los datos de la empresa como razón social, NIT y dirección se ven en Administración > Empresa."),
    ];

    private static readonly HashSet<string> s_stopWords = ["que", "los", "las", "del", "por", "con", "una", "para", "como", "esta", "use"];

    public static JsonObject Respond(string message)
    {
        var query = Words(message).ToHashSet();
        var documents = s_knowledge
            .Select(d => (d.Source, d.Text, Score: Words(d.Text).Count(query.Contains)))
            .Where(d => d.Score > 0)
            .OrderByDescending(d => d.Score)
            .Take(2)
            .ToList();

        var observation = new JsonArray(documents
            .Select(d => (JsonNode)new JsonObject
            {
                ["type"] = "text",
                ["text"] = new JsonObject { ["pageContent"] = d.Text, ["metadata"] = new JsonObject { ["source"] = d.Source } }.ToJsonString(),
            })
            .ToArray());

        bool technicalIncident = System.Text.RegularExpressions.Regex.IsMatch(message, @"error \d{3}|falla", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        bool canAnswer = !technicalIncident && documents.Count > 0 && documents[0].Score >= 2;

        return new JsonObject
        {
            ["output"] = new JsonObject
            {
                ["puede_responder"] = canAnswer,
                ["respuesta"] = canAnswer ? documents[0].Text : "No tengo información suficiente para resolverlo; lo escalo a segundo nivel.",
            },
            ["intermediateSteps"] = new JsonArray(new JsonObject
            {
                ["action"] = new JsonObject { ["tool"] = "base_conocimiento", ["toolInput"] = new JsonObject { ["input"] = message } },
                ["observation"] = observation.ToJsonString(),
            }),
        };
    }

    // Sin tildes y recortadas a 6 letras para que "bloqueado" encuentre "bloquea".
    private static IEnumerable<string> Words(string text) =>
        System.Text.RegularExpressions.Regex.Matches(CheckRunner.Normalize(text), "[a-z0-9]{3,}")
            .Select(m => m.Value)
            .Where(w => !s_stopWords.Contains(w))
            .Select(w => w.Length > 6 ? w[..6] : w);
}

public static class DemoSeeder
{
    public static async Task SeedAsync(EvalDbContext db, string baseUrl)
    {
        if (await db.Agents.AnyAsync())
        {
            return;
        }

        var agent = new Agent
        {
            Name = "Demo · helpdesk simulado (estilo n8n)",
            Url = $"{baseUrl.TrimEnd('/')}/demo/helpdesk",
            BodyTemplate = """{ "mensaje": "{{input}}", "ticket_id": "{{id}}" }""",
            ResponsePath = "output.respuesta",
            ContextPath = "intermediateSteps[*].observation",
            ToolsPath = "intermediateSteps[*].action.tool",
            TimeoutSeconds = 60,
        };

        await using Stream csv = typeof(DemoSeeder).Assembly.GetManifestResourceStream("tickets.csv")!;
        ImportedTable table = DatasetImporter.FromCsv(csv);
        var dataset = new Dataset
        {
            Name = "Demo · tickets de soporte",
            SourceFileName = "tickets.csv",
            Columns = table.Columns,
            Rows = table.Rows,
        };

        db.Agents.Add(agent);
        db.Datasets.Add(dataset);
        await db.SaveChangesAsync();

        db.Evaluations.Add(new Evaluation
        {
            Name = "Demo · ¿la base de conocimiento alcanza para N1?",
            AgentId = agent.Id,
            DatasetId = dataset.Id,
            InputColumn = "ticket",
            Checks =
            [
                new() { Type = CheckTypes.Exact, Label = "Decisión: responder o escalar", ResponsePath = "output.puede_responder", ExpectedColumn = "debe_responder" },
                new() { Type = CheckTypes.Correctness, ExpectedColumn = "respuesta_referencia" },
                new() { Type = CheckTypes.Coverage, ExpectedColumn = "respuesta_referencia", Blocking = false },
                new() { Type = CheckTypes.Groundedness, Blocking = false },
                new() { Type = CheckTypes.Latency, Threshold = 15000 },
            ],
        });
        await db.SaveChangesAsync();
    }
}
