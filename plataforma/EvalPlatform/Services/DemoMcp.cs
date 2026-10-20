using System.ComponentModel;
using System.Globalization;
using EvalPlatform.Data;
using EvalPlatform.Domain;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace EvalPlatform.Services;

// Servidor MCP de demostración (/demo/mcp) con datos inventados de cartera y soporte.
// buscar_cliente distingue mayúsculas a propósito: es el "error" que encuentra la evaluación de ejemplo.
[McpServerToolType]
public sealed class DemoMcpTools
{
    private static readonly Dictionary<string, (string Nombre, decimal Saldo, int Vencidas)> s_clientes = new()
    {
        ["123"] = ("Constructora Andina", 1_200_000m, 2),
        ["456"] = ("Inversiones del Valle", 0m, 0),
    };

    private static int s_ticket = 1000;

    [McpServerTool(Name = "consultar_cartera", ReadOnly = true)]
    [Description("Consulta el saldo pendiente y las facturas vencidas de un cliente por su código.")]
    public static string ConsultarCartera([Description("Código del cliente, por ejemplo 123")] string cliente)
    {
        if (!s_clientes.TryGetValue(cliente.Trim(), out var c))
        {
            throw new McpException($"El cliente {cliente} no existe.");
        }
        return c.Saldo == 0
            ? $"El cliente {cliente} ({c.Nombre}) está al día."
            : $"El cliente {cliente} ({c.Nombre}) debe {Pesos(c.Saldo)}, con {c.Vencidas} facturas vencidas.";
    }

    [McpServerTool(Name = "buscar_cliente", ReadOnly = true)]
    [Description("Busca el código de un cliente a partir de su nombre o razón social.")]
    public static string BuscarCliente([Description("Nombre o razón social del cliente")] string nombre)
    {
        var match = s_clientes.FirstOrDefault(kv => kv.Value.Nombre == nombre.Trim());
        return match.Key is null
            ? throw new McpException($"No se encontró un cliente llamado '{nombre}'.")
            : $"{match.Value.Nombre} tiene el código {match.Key}.";
    }

    [McpServerTool(Name = "crear_ticket")]
    [Description("Crea un ticket de soporte para que lo atienda una persona del equipo de segundo nivel.")]
    public static string CrearTicket(
        [Description("Resumen del problema")] string asunto,
        [Description("Prioridad: baja, media o alta")] string prioridad = "media")
    {
        if (string.IsNullOrWhiteSpace(asunto))
        {
            throw new McpException("El asunto del ticket es obligatorio.");
        }
        string p = prioridad.Trim().ToLowerInvariant();
        if (p is not ("baja" or "media" or "alta"))
        {
            throw new McpException($"La prioridad '{prioridad}' no es válida. Usa baja, media o alta.");
        }
        int id = Interlocked.Increment(ref s_ticket);
        return $"Ticket T-{id} creado con prioridad {p}: {asunto.Trim()}";
    }

    private static string Pesos(decimal value) => value.ToString("$#,##0", CultureInfo.GetCultureInfo("es-CO"));
}

public static class DemoMcpSeeder
{
    public const string DirectAgentName = "Demo · servidor MCP (llamada directa)";
    public const string ModelAgentName = "Demo · modelo + servidor MCP";

    public static async Task SeedAsync(EvalDbContext db, string baseUrl)
    {
        if (await db.Agents.AnyAsync(a => a.Name == DirectAgentName))
        {
            return;
        }
        string url = $"{baseUrl.TrimEnd('/')}/demo/mcp";

        var direct = new Agent { Name = DirectAgentName, Kind = AgentKinds.McpDirect, Url = url, TimeoutSeconds = 30, Mcp = new McpSettings() };
        var withModel = new Agent
        {
            Name = ModelAgentName,
            Kind = AgentKinds.McpModel,
            Url = url,
            TimeoutSeconds = 120,
            Mcp = new McpSettings { SystemPrompt = "Eres el asistente de cartera y soporte. Usa las herramientas para consultar datos; no inventes cifras. Responde en español, breve." },
        };

        var directCases = new Dataset
        {
            Name = "Demo · casos del servidor MCP",
            SourceFileName = "ejemplo",
            Columns = ["id", "caso", "herramienta", "argumentos", "debe_contener", "es_error"],
            Rows =
            [
                Row("M-01", "Saldo de un cliente existente", "consultar_cartera", """{"cliente": "123"}""", "1.200.000", "no"),
                Row("M-02", "Cliente que no existe", "consultar_cartera", """{"cliente": "999"}""", "no existe", "si"),
                Row("M-03", "Buscar cliente por nombre", "buscar_cliente", """{"nombre": "Constructora Andina"}""", "123", "no"),
                Row("M-04", "Buscar cliente escrito en minúsculas", "buscar_cliente", """{"nombre": "constructora andina"}""", "123", "no"),
                Row("M-05", "Crear ticket con prioridad alta", "crear_ticket", """{"asunto": "No carga la nómina", "prioridad": "alta"}""", "T-|alta", "no"),
                Row("M-06", "Ticket sin asunto debe fallar", "crear_ticket", """{"asunto": ""}""", "obligatorio", "si"),
                Row("M-07", "Prioridad inválida debe fallar", "crear_ticket", """{"asunto": "Prueba", "prioridad": "urgentísima"}""", "no es válida", "si"),
            ],
        };

        var modelCases = new Dataset
        {
            Name = "Demo · preguntas para el modelo con MCP",
            SourceFileName = "ejemplo",
            Columns = ["id", "input", "herramientas_esperadas", "argumentos_esperados", "respuesta_esperada"],
            Rows =
            [
                new() { ["id"] = "Q-01", ["input"] = "¿Cuánto debe el cliente 123?", ["herramientas_esperadas"] = "consultar_cartera", ["argumentos_esperados"] = """consultar_cartera {"cliente": "123"}""", ["respuesta_esperada"] = "El cliente 123 debe $1.200.000 y tiene 2 facturas vencidas." },
                new() { ["id"] = "Q-02", ["input"] = "¿Cuánto nos debe Constructora Andina?", ["herramientas_esperadas"] = "buscar_cliente|consultar_cartera", ["argumentos_esperados"] = """consultar_cartera {"cliente": "123"}""", ["respuesta_esperada"] = "Constructora Andina (código 123) debe $1.200.000." },
                new() { ["id"] = "Q-03", ["input"] = "Abre un ticket urgente: no puedo generar la nómina", ["herramientas_esperadas"] = "crear_ticket", ["argumentos_esperados"] = """crear_ticket {"prioridad": "alta"}""", ["respuesta_esperada"] = "Se creó un ticket con prioridad alta." },
                new() { ["id"] = "Q-04", ["input"] = "¿Inversiones del Valle tiene deudas?", ["herramientas_esperadas"] = "buscar_cliente|consultar_cartera", ["argumentos_esperados"] = """consultar_cartera {"cliente": "456"}""", ["respuesta_esperada"] = "Inversiones del Valle está al día." },
                new() { ["id"] = "Q-05", ["input"] = "Hola, ¿qué puedes hacer?", ["herramientas_esperadas"] = "ninguna", ["argumentos_esperados"] = "", ["respuesta_esperada"] = "Explica que puede consultar cartera, buscar clientes y crear tickets, sin llamar herramientas." },
            ],
        };

        db.AddRange(direct, withModel, directCases, modelCases);
        await db.SaveChangesAsync();

        db.Evaluations.AddRange(
            new Evaluation
            {
                Name = "Demo · MCP: ¿las herramientas responden bien?",
                AgentId = direct.Id,
                DatasetId = directCases.Id,
                InputColumn = "caso",
                Checks =
                [
                    new() { Type = CheckTypes.Contains, ExpectedColumn = "debe_contener" },
                    new() { Type = CheckTypes.Exact, Label = "¿Debía dar error?", ResponsePath = "isError", ExpectedColumn = "es_error" },
                    new() { Type = CheckTypes.Latency, Threshold = 5000 },
                ],
            },
            new Evaluation
            {
                Name = "Demo · MCP: ¿el modelo elige bien las herramientas?",
                AgentId = withModel.Id,
                DatasetId = modelCases.Id,
                InputColumn = "input",
                Checks =
                [
                    new() { Type = CheckTypes.Tools, ExpectedColumn = "herramientas_esperadas" },
                    new() { Type = CheckTypes.ToolArguments, ExpectedColumn = "argumentos_esperados" },
                    new() { Type = CheckTypes.Correctness, ExpectedColumn = "respuesta_esperada" },
                    new() { Type = CheckTypes.Groundedness, Blocking = false },
                ],
            });
        await db.SaveChangesAsync();
    }

    private static Dictionary<string, string> Row(string id, string caso, string herramienta, string argumentos, string contiene, string error) =>
        new() { ["id"] = id, ["caso"] = caso, ["herramienta"] = herramienta, ["argumentos"] = argumentos, ["debe_contener"] = contiene, ["es_error"] = error };
}
