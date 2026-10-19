using System.Globalization;
using CsvHelper;
using CsvHelper.Configuration.Attributes;

namespace AgentEvals.Tests;

// Una fila de dataset/casos.csv (o de la hoja de Google Sheets exportada a CSV).
public sealed class EvalCase
{
    [Name("id")] public string Id { get; init; } = "";
    [Name("categoria")] public string Category { get; init; } = "";
    [Name("input")] public string Input { get; init; } = "";
    [Name("expected_output")] public string ExpectedOutput { get; init; } = "";
    [Name("must_contain")] public string MustContain { get; init; } = "";
    [Name("must_not_contain")] public string MustNotContain { get; init; } = "";
    [Name("expected_tools")] public string ExpectedTools { get; init; } = "";
    [Name("rubric")] public string Rubric { get; init; } = "";

    public static IReadOnlyList<string> SplitList(string value) =>
        value.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public override string ToString() => Id;
}

public static class EvalDataset
{
    // EVAL_DATASET permite apuntar a otro CSV sin recompilar.
    private static readonly Lazy<IReadOnlyDictionary<string, EvalCase>> s_cases = new(() =>
    {
        string path = Environment.GetEnvironmentVariable("EVAL_DATASET")
            ?? Path.Combine(AppContext.BaseDirectory, "casos.csv");

        using var reader = new StreamReader(path);
        using var csv = new CsvReader(reader, CultureInfo.InvariantCulture);
        return csv.GetRecords<EvalCase>().ToDictionary(c => c.Id);
    });

    public static EvalCase Get(string id) => s_cases.Value[id];

    public static TheoryData<string> Ids => new(s_cases.Value.Keys);
}
