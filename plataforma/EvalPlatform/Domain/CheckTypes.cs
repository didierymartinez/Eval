namespace EvalPlatform.Domain;

public enum CheckFamily { Text, Behavior, AiJudge, Rag }

// Catálogo de comparaciones disponibles. La interfaz lo pide en GET /api/comparaciones,
// así que agregar un tipo aquí (y en CheckRunner) lo muestra en pantalla.
public sealed record CheckTypeInfo(
    string Type,
    string Label,
    string Description,
    CheckFamily Family,
    bool NeedsExpected,
    bool UsesJudge,
    double? DefaultThreshold,
    string? ThresholdHelp);

public static class CheckTypes
{
    public const string Exact = "igual";
    public const string Contains = "contiene";
    public const string NotContains = "no_contiene";
    public const string Similarity = "similitud";
    public const string Regex = "regex";
    public const string Tools = "herramientas";
    public const string ToolsInOrder = "herramientas_orden";
    public const string Tokens = "tokens";
    public const string Latency = "latencia";
    public const string Correctness = "correccion";
    public const string Relevance = "relevancia";
    public const string Rubric = "rubrica";
    public const string Groundedness = "fidelidad";
    public const string Coverage = "cobertura";

    public static readonly IReadOnlyList<CheckTypeInfo> All =
    [
        new(Exact, "Es igual a",
            "La respuesta (o el campo elegido) coincide con lo esperado, sin importar mayúsculas ni espacios. 'si', 'sí', 'true' y '1' cuentan como lo mismo.",
            CheckFamily.Text, NeedsExpected: true, UsesJudge: false, null, null),
        new(Contains, "Contiene",
            "La respuesta incluye todos los textos esperados. Separa varios con |  (ejemplo: 12345|en camino).",
            CheckFamily.Text, true, false, null, null),
        new(NotContains, "No contiene",
            "La respuesta no incluye ninguno de los textos. Separa varios con |.",
            CheckFamily.Text, true, false, null, null),
        new(Similarity, "Se parece a (texto)",
            "Qué tan parecido es el texto a lo esperado, letra por letra (0 a 1). Útil para respuestas cortas y casi fijas.",
            CheckFamily.Text, true, false, 0.8, "Similitud mínima entre 0 y 1"),
        new(Regex, "Cumple un patrón",
            "La respuesta cumple una expresión regular (para formatos: números de ticket, fechas, correos).",
            CheckFamily.Text, true, false, null, null),
        new(Tools, "Usó las herramientas",
            "El agente llamó todas las herramientas esperadas (separadas por |; el orden no importa). 'ninguna' = no debía llamar herramientas. Requiere configurar en el agente dónde vienen las herramientas.",
            CheckFamily.Behavior, true, false, null, null),
        new(ToolsInOrder, "Usó las herramientas en orden",
            "Como la anterior, pero las herramientas deben aparecer en ese orden (puede haber otras en medio). Útil cuando importa la secuencia: por ejemplo, buscar_pedido antes de crear_ticket.",
            CheckFamily.Behavior, true, false, null, null),
        new(Tokens, "Consume pocos tokens",
            "El agente usó como máximo esa cantidad de tokens. Requiere configurar en el agente dónde vienen los tokens.",
            CheckFamily.Behavior, false, false, 4000, "Tokens máximos"),
        new(Latency, "Responde a tiempo",
            "El agente respondió en menos del tiempo indicado.",
            CheckFamily.Behavior, false, false, 15000, "Milisegundos máximos"),
        new(Correctness, "Correcta frente a lo esperado (IA)",
            "Un modelo juez compara el significado de la respuesta con la respuesta esperada, de 1 a 5. No exige el mismo texto.",
            CheckFamily.AiJudge, true, true, 4, "Puntaje mínimo de 1 a 5"),
        new(Relevance, "Responde lo que se preguntó (IA)",
            "Un modelo juez califica si la respuesta atiende la pregunta, de 1 a 5. No necesita respuesta esperada.",
            CheckFamily.AiJudge, false, true, 4, "Puntaje mínimo de 1 a 5"),
        new(Rubric, "Cumple un criterio (IA)",
            "Un modelo juez revisa un criterio escrito en palabras (ejemplo: 'saluda, pide el número de pedido y no promete fechas'), de 1 a 5.",
            CheckFamily.AiJudge, true, true, 4, "Puntaje mínimo de 1 a 5"),
        new(Groundedness, "Respaldada por los documentos (RAG)",
            "Un modelo juez revisa si lo que respondió está en los documentos que recuperó el agente, o si lo inventó. Requiere configurar en el agente dónde viene el contexto.",
            CheckFamily.Rag, false, true, 4, "Puntaje mínimo de 1 a 5"),
        new(Coverage, "Los documentos traen la respuesta (RAG)",
            "Un modelo juez revisa si los documentos que recuperó el agente contienen la respuesta esperada. Sirve para saber si a la base de conocimiento le falta información.",
            CheckFamily.Rag, true, true, 4, "Puntaje mínimo de 1 a 5"),
    ];

    public static CheckTypeInfo? Find(string type) => All.FirstOrDefault(t => t.Type == type);
}
