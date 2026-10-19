using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace AgentEvals.Tests;

// Llama al agente por URL usando el contrato de samples/AgentEndpoint/Contracts.cs
// y traduce la respuesta a tipos de Microsoft.Extensions.AI para poder evaluarla.
public sealed class AgentClient
{
    private static readonly HttpClient s_http = new() { Timeout = TimeSpan.FromMinutes(2) };

    private readonly Uri _url = new(Environment.GetEnvironmentVariable("AGENT_URL") ?? "http://localhost:5150/eval/run");
    private readonly string _apiKey = Environment.GetEnvironmentVariable("AGENT_EVAL_KEY") ?? "dev-eval-key";

    public async Task<(IList<ChatMessage> Messages, ChatResponse Response, AgentRunResult Raw)> RunAsync(
        EvalCase evalCase, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, _url)
        {
            Content = JsonContent.Create(new { input = evalCase.Input, context = new { caso = evalCase.Id } }),
        };
        request.Headers.Add("X-Eval-Key", _apiKey);

        using HttpResponseMessage http = await s_http.SendAsync(request, cancellationToken);
        http.EnsureSuccessStatusCode();
        AgentRunResult raw = (await http.Content.ReadFromJsonAsync<AgentRunResult>(JsonSerializerOptions.Web, cancellationToken))!;

        // Las llamadas a herramientas viajan como FunctionCallContent, igual que en
        // una respuesta real de MEAI; así los evaluadores las pueden leer.
        var contents = new List<AIContent>();
        foreach (ToolCall call in raw.ToolCalls)
        {
            contents.Add(new FunctionCallContent(callId: Guid.NewGuid().ToString("N"), name: call.Name, arguments: call.Arguments));
        }
        contents.Add(new TextContent(raw.Output));

        IList<ChatMessage> messages = [new ChatMessage(ChatRole.User, evalCase.Input)];
        var response = new ChatResponse(new ChatMessage(ChatRole.Assistant, contents));
        return (messages, response, raw);
    }
}

public sealed record AgentRunResult(string Output, IReadOnlyList<ToolCall> ToolCalls, long LatencyMs, string? TraceId);

public sealed record ToolCall(string Name, Dictionary<string, object?>? Arguments);
