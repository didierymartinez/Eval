using EvalPlatform.Services;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation;

namespace EvalPlatform.Tests;

// Juez de mentira: responde con el formato <S0>/<S1>/<S2> que esperan los evaluadores.
// El puntaje lo decide la función que recibe el prompt.
public sealed class FakeJudgeClient(Func<string, int> score) : IChatClient
{
    public List<string> Prompts { get; } = [];

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        string prompt = string.Join("\n", messages.Select(m => m.Text));
        lock (Prompts)
        {
            Prompts.Add(prompt);
        }
        int s = score(prompt);
        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, $"<S0>pensando</S0><S1>Puntaje {s} del juez de prueba.</S1><S2>{s}</S2>")));
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }
}

public sealed class FakeJudgeProvider(IChatClient? client) : IJudgeProvider
{
    private readonly ChatConfiguration? _configuration = client is null ? null : new ChatConfiguration(client);

    public ChatConfiguration? Get() => _configuration;

    public string? ModelName => _configuration is null ? null : "juez-de-prueba";
}
