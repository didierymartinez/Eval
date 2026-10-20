// Normaliza la respuesta del webhook del agente para el eval de helpdesk.
//
// Se espera que el agente responda con su salida y sus pasos intermedios (formato de
// agentes con herramientas, como LangChain):
//   { "output": { "puede_responder": true, "respuesta": "..." }, "intermediateSteps": [...] }
// "output" también puede ser texto plano o un JSON en texto. Si el agente arma
// su propio JSON, se aceptan "respuesta", "puede_responder" y "contextos" en la raíz.
//
// Devuelve:
//   output                 el texto de la respuesta al cliente
//   metadata.puedeResponder  true / false (o inferido del texto si el agente no lo dice)
//   metadata.contextos       textos que recuperó del vector store (para cobertura y fidelidad)

// Si el agente no devuelve puede_responder, se infiere de frases de escalamiento.
const ESCALAMIENTO = new RegExp(
  process.env.ESCALATION_REGEX ||
    'escal|segundo nivel|un asesor|un agente humano|no (tengo|cuento con|encuentro) (la |esa )?informaci',
  'i',
);

const tryParse = (value) => {
  if (typeof value !== 'string') return value;
  try {
    return JSON.parse(value);
  } catch {
    return value;
  }
};

// Las observaciones de la herramienta de vector store llegan con formas distintas
// según el orquestador y la herramienta: texto, [{type:'text', text:'{"pageContent":...}'}],
// [{pageContent, metadata}], etc. Se aplanan a una lista de textos.
const toTexts = (value) => {
  value = tryParse(value);
  if (value == null) return [];
  if (Array.isArray(value)) return value.flatMap(toTexts);
  if (typeof value === 'string') return value.trim() ? [value.trim()] : [];
  if (typeof value === 'object') {
    if ('pageContent' in value) return toTexts(value.pageContent);
    if ('text' in value) return toTexts(value.text);
    if ('content' in value) return toTexts(value.content);
    return [JSON.stringify(value)];
  }
  return [String(value)];
};

module.exports = (json, text) => {
  let body = Array.isArray(json) ? json[0] : json;
  if (body && typeof body === 'object' && body.json) body = body.json;
  if (body == null || typeof body !== 'object') body = { output: text };

  let out = tryParse(body.output ?? body.respuesta ?? text);
  if (out && typeof out === 'object' && out.output !== undefined) out = tryParse(out.output);

  const respuesta =
    out && typeof out === 'object' ? String(out.respuesta ?? out.answer ?? JSON.stringify(out)) : String(out ?? '');

  let puedeResponder =
    out && typeof out === 'object' && typeof out.puede_responder === 'boolean'
      ? out.puede_responder
      : typeof body.puede_responder === 'boolean'
        ? body.puede_responder
        : null;
  const decisionInferida = puedeResponder === null;
  if (decisionInferida) puedeResponder = !ESCALAMIENTO.test(respuesta);

  const steps = body.intermediateSteps ?? [];
  const contextos = [...steps.flatMap((s) => toTexts(s.observation)), ...toTexts(body.contextos)];
  const herramientas = steps.map((s) => s.action?.tool).filter(Boolean);

  return {
    output: respuesta,
    metadata: { puedeResponder, decisionInferida, contextos, herramientas },
  };
};
