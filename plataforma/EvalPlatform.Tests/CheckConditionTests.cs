using System.Text.Json.Nodes;
using EvalPlatform.Domain;
using EvalPlatform.Services;

namespace EvalPlatform.Tests;

public class CheckConditionTests
{
    private static CaseData Caso(string json, string output) => new(
        new Agent { Name = "agente", Kind = AgentKinds.Http },
        "¿Cómo corrijo el precio?",
        new Dictionary<string, string> { ["referencia"] = "Registre una nota en valor." },
        new AgentInvocation { Json = JsonNode.Parse(json), Output = output });

    [Fact]
    public async Task Se_omite_cuando_no_se_cumple_la_condicion_y_no_llama_al_juez()
    {
        var juez = new FakeJudgeClient(_ => 5);
        var runner = new CheckRunner(new FakeJudgeProvider(juez));
        var check = new CheckDefinition
        {
            Type = CheckTypes.Correctness, ExpectedColumn = "referencia",
            OnlyWhenPath = "puede_responder", OnlyWhenValue = "sí",
        };

        var escalado = await runner.RunAsync(check, Caso("""{"puede_responder": false, "respuesta": "Un consultor revisará su caso."}""", "Un consultor revisará su caso."), default);

        Assert.Equal(CheckStatus.Skipped, escalado.Status);
        Assert.Contains("puede_responder", escalado.Reason);
        Assert.Empty(juez.Prompts);
    }

    [Fact]
    public async Task Se_evalua_cuando_se_cumple_la_condicion_y_el_juez_recibe_las_instrucciones()
    {
        var juez = new FakeJudgeClient(_ => 4);
        var runner = new CheckRunner(new FakeJudgeProvider(juez));
        var check = new CheckDefinition
        {
            Type = CheckTypes.Correctness, ExpectedColumn = "referencia",
            OnlyWhenPath = "puede_responder", OnlyWhenValue = "sí",
            JudgeInstructions = "No exijas datos puntuales del cliente.",
        };

        var respondido = await runner.RunAsync(check, Caso("""{"puede_responder": true, "respuesta": "Use una nota en valor."}""", "Use una nota en valor."), default);

        Assert.Equal(CheckStatus.Passed, respondido.Status);
        var prompt = Assert.Single(juez.Prompts);
        Assert.Contains("INSTRUCCIONES ADICIONALES DE ESTA EVALUACIÓN", prompt);
        Assert.Contains("No exijas datos puntuales del cliente.", prompt);
        Assert.True(prompt.IndexOf("No exijas", StringComparison.Ordinal) < prompt.IndexOf("Responde exactamente con este formato", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Sin_instrucciones_el_prompt_no_cambia()
    {
        var juez = new FakeJudgeClient(_ => 4);
        var runner = new CheckRunner(new FakeJudgeProvider(juez));
        await runner.RunAsync(new CheckDefinition { Type = CheckTypes.Correctness, ExpectedColumn = "referencia" },
            Caso("""{"respuesta": "x"}""", "x"), default);
        Assert.DoesNotContain("INSTRUCCIONES ADICIONALES", Assert.Single(juez.Prompts));
    }
}
