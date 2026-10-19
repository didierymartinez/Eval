using System.Text.RegularExpressions;

namespace AgentEndpoint;

// Lo que el endpoint necesita de tu agente real: una entrada y, de vuelta,
// la respuesta final más las herramientas que llamó.
public interface IAgent
{
    Task<AgentResult> RunAsync(EvalRunRequest request, CancellationToken cancellationToken);
}

public sealed record AgentResult(string Output, IReadOnlyList<ToolCallRecord> ToolCalls, UsageInfo? Usage);

// Agente de mentira, basado en reglas, para poder probar el arnés sin
// llaves de API. Reemplázalo por tu agente (Semantic Kernel, MEAI,
// Agent Framework, llamadas directas al proveedor, etc.).
// El caso de cambio de dirección falla a propósito: nunca llama
// actualizar_direccion, para que veas cómo se ve un fallo en el reporte.
public sealed partial class FakeAgent : IAgent
{
    public Task<AgentResult> RunAsync(EvalRunRequest request, CancellationToken cancellationToken)
    {
        string input = request.Input;
        var tools = new List<ToolCallRecord>();
        string output;

        if (OrderNumber().Match(input) is { Success: true } match)
        {
            string order = match.Value;
            tools.Add(Tool("buscar_pedido", ("pedido", order), $"{{\"pedido\":\"{order}\",\"estado\":\"en_camino\",\"entrega\":\"2026-10-22\"}}"));

            if (input.Contains("devolver", StringComparison.OrdinalIgnoreCase))
            {
                tools.Add(Tool("crear_ticket", ("pedido", order), "{\"ticket\":\"T-9001\"}"));
                output = $"Lamento que tu producto llegara roto. Abrí el ticket T-9001 de devolución para el pedido {order}; te escribiremos por correo con la guía de envío.";
            }
            else if (input.Contains("dirección", StringComparison.OrdinalIgnoreCase))
            {
                output = $"Revisé el pedido {order}: está en camino y llega el 22 de octubre.";
            }
            else
            {
                output = $"Tu pedido {order} está en camino y llega el 22 de octubre.";
            }
        }
        else if (input.Contains("contraseña", StringComparison.OrdinalIgnoreCase))
        {
            output = "Lo siento, no puedo compartir esa información. ¿Te ayudo con algo de tu pedido?";
        }
        else if (input.Contains("horario", StringComparison.OrdinalIgnoreCase))
        {
            output = "Atendemos de lunes a viernes de 8:00 a 18:00.";
        }
        else
        {
            output = "¡Hola! ¿En qué puedo ayudarte con tu pedido?";
        }

        var usage = new UsageInfo(InputTokens: input.Length / 4 + 200, OutputTokens: output.Length / 4);
        return Task.FromResult(new AgentResult(output, tools, usage));
    }

    private static ToolCallRecord Tool(string name, (string Key, object? Value) argument, string result) =>
        new(name, new Dictionary<string, object?> { [argument.Key] = argument.Value }, result);

    [GeneratedRegex(@"\b\d{3,6}\b")]
    private static partial Regex OrderNumber();
}
