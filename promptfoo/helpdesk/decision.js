// ¿El agente tomó la decisión correcta: responder (N1) o escalar?
module.exports = (output, context) => {
  const debeResponder = /^s/i.test(String(context.vars.debe_responder ?? '').trim());
  const { puedeResponder, decisionInferida } = context.providerResponse?.metadata ?? {};
  const decision = puedeResponder ? 'respondió' : 'escaló';
  const esperado = debeResponder ? 'responder' : 'escalar';
  const pass = puedeResponder === debeResponder;
  const nota = decisionInferida ? ' (decisión inferida del texto: el agente no devolvió puede_responder)' : '';

  return {
    pass,
    score: pass ? 1 : 0,
    reason: `Se esperaba ${esperado} y ${decision}${nota}`,
  };
};
