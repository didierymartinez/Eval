// Chequeos deterministas para cada fila del dataset (dataset/casos.csv).
// Mismas reglas que dotnet/AgentEvals.Tests/DeterministicEvaluators.cs:
//   must_contain      valores separados por "|"; todos deben aparecer (sin distinguir mayúsculas)
//   must_not_contain  valores separados por "|"; ninguno debe aparecer
//   expected_tools    herramientas separadas por "|" que deben llamarse (el orden no importa,
//                     se permiten extras); "ninguna" = no debe llamar herramientas; vacío = no se revisa

const split = (value) =>
  String(value ?? '')
    .split('|')
    .map((v) => v.trim())
    .filter(Boolean);

module.exports = (output, context) => {
  const vars = context.vars;
  const text = String(output ?? '').toLowerCase();
  const toolCalls = context.providerResponse?.metadata?.toolCalls ?? [];
  const called = toolCalls.map((t) => t.name);
  const results = [];

  for (const value of split(vars.must_contain)) {
    const pass = text.includes(value.toLowerCase());
    results.push({ pass, score: pass ? 1 : 0, reason: `${pass ? 'Contiene' : 'Falta'} "${value}"` });
  }

  for (const value of split(vars.must_not_contain)) {
    const pass = !text.includes(value.toLowerCase());
    results.push({ pass, score: pass ? 1 : 0, reason: `${pass ? 'No contiene' : 'Contiene prohibido'} "${value}"` });
  }

  const expectedTools = split(vars.expected_tools);
  if (expectedTools.length === 1 && expectedTools[0].toLowerCase() === 'ninguna') {
    const pass = called.length === 0;
    results.push({ pass, score: pass ? 1 : 0, reason: pass ? 'No llamó herramientas' : `Llamó herramientas: ${called.join(', ')}` });
  } else if (expectedTools.length > 0) {
    const missing = expectedTools.filter((t) => !called.includes(t));
    const pass = missing.length === 0;
    results.push({
      pass,
      score: (expectedTools.length - missing.length) / expectedTools.length,
      reason: pass ? `Llamó ${expectedTools.join(', ')}` : `Faltó llamar: ${missing.join(', ')} (llamó: ${called.join(', ') || 'nada'})`,
    });
  }

  if (results.length === 0) {
    return { pass: true, score: 1, reason: 'Sin chequeos deterministas para este caso' };
  }

  const pass = results.every((r) => r.pass);
  return {
    pass,
    score: results.reduce((sum, r) => sum + r.score, 0) / results.length,
    reason: results.filter((r) => !r.pass).map((r) => r.reason).join('; ') || 'Todos los chequeos deterministas pasaron',
    componentResults: results,
  };
};
