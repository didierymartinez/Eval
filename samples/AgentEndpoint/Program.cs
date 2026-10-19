using System.Diagnostics;
using AgentEndpoint;

var builder = WebApplication.CreateBuilder(args);

// Cambia FakeAgent por tu agente real.
builder.Services.AddSingleton<IAgent, FakeAgent>();

var app = builder.Build();

app.MapGet("/", () => "AgentEndpoint: POST /eval/run");

// Endpoint de evaluación. Solo se expone cuando Eval:Enabled = true
// (appsettings.Development.json) y exige el header X-Eval-Key.
if (app.Configuration.GetValue<bool>("Eval:Enabled"))
{
    string apiKey = app.Configuration["Eval:ApiKey"]
        ?? throw new InvalidOperationException("Configura Eval:ApiKey.");

    app.MapPost("/eval/run", async (EvalRunRequest request, HttpRequest http, IAgent agent, CancellationToken ct) =>
    {
        if (http.Headers["X-Eval-Key"] != apiKey)
        {
            return Results.Unauthorized();
        }

        var stopwatch = Stopwatch.StartNew();
        AgentResult result = await agent.RunAsync(request, ct);
        stopwatch.Stop();

        return Results.Ok(new EvalRunResponse(
            result.Output,
            result.ToolCalls,
            result.Usage,
            stopwatch.ElapsedMilliseconds,
            Activity.Current?.TraceId.ToString()));
    });
}

app.Run();
