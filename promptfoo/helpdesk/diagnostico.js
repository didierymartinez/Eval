// Convierte los resultados de promptfoo en un diagnóstico por ticket:
// ¿la base de conocimiento alcanza, falta información, o el problema es el agente?
//
// Uso: node diagnostico.js resultados.json [diagnostico.csv]

const fs = require('fs');

const UMBRAL_COBERTURA = Number(process.env.UMBRAL_COBERTURA || 0.6);
const UMBRAL_FIDELIDAD = Number(process.env.UMBRAL_FIDELIDAD || 0.7);

const DIAGNOSTICOS = {
  CUBIERTO: {
    titulo: 'Cubierto',
    accion: 'Nada. La base tiene la información y el agente la usa bien.',
  },
  BRECHA: {
    titulo: 'Brecha de conocimiento',
    accion:
      'El agente no recuperó la información y escaló. Busca la respuesta de referencia en los documentos fuente: si no está, agrégala a la base; si está, revisa chunking, embeddings o top-k.',
  },
  BRECHA_PARCIAL: {
    titulo: 'Brecha parcial',
    accion: 'Respondió con lo que encontró, pero a la base le falta parte de la respuesta. Completa el documento.',
  },
  ALUCINACION: {
    titulo: 'Alucinación',
    accion: 'Respondió con información que no está en lo recuperado. Refuerza en el prompt que solo responda con el contexto y que escale si no alcanza.',
  },
  FALLA_AGENTE: {
    titulo: 'Falla del agente',
    accion: 'La información se recuperó pero la respuesta es incorrecta o incompleta. Revisa el prompt o el modelo.',
  },
  CONSERVADOR: {
    titulo: 'Escaló sin necesidad',
    accion: 'La información estaba en lo recuperado y aun así escaló. Revisa el criterio de decisión del prompt.',
  },
  ESCALADO_OK: {
    titulo: 'Escalado correcto',
    accion: 'Nada. Era un caso de segundo nivel y lo escaló.',
  },
  NO_DEBIA_RESPONDER: {
    titulo: 'Respondió un caso de N2',
    accion: 'Debía escalar. Agrega al prompt la regla de escalamiento para este tipo de caso.',
  },
  ERROR: {
    titulo: 'Error de ejecución',
    accion: 'El webhook o el juez fallaron. Revisa el detalle en promptfoo view.',
  },
};

// cobertura y fiel van con weight 0, así que promptfoo las marca como aprobadas aunque
// el juez falle. Si el motivo no es el normal ("Recall 0.40 is < 0.6"), el juez no
// llegó a calificar y el ticket se marca ERROR en vez de BRECHA.
const juezFallo = (c) => c && !/^(Recall|Faithfulness) /.test(c.reason ?? '');

function clasificar(r) {
  if (r.response?.error || (r.error && !r.response)) return 'ERROR';

  const debeResponder = /^s/i.test(String(r.vars?.debe_responder ?? ''));
  const respondio = r.response?.metadata?.puedeResponder === true;
  const metricas = Object.fromEntries(
    (r.gradingResult?.componentResults ?? []).map((c) => [c.assertion?.metric, c]),
  );
  if (juezFallo(metricas.cobertura) || juezFallo(metricas.fiel)) return 'ERROR';

  const cobertura = metricas.cobertura?.score ?? 0;
  const fiel = metricas.fiel?.score ?? 0;
  const correcta = metricas.correcta?.pass === true;

  if (!debeResponder) return respondio ? 'NO_DEBIA_RESPONDER' : 'ESCALADO_OK';
  if (!respondio) return cobertura >= UMBRAL_COBERTURA ? 'CONSERVADOR' : 'BRECHA';
  if (fiel < UMBRAL_FIDELIDAD) return 'ALUCINACION';
  if (correcta) return 'CUBIERTO';
  return cobertura >= UMBRAL_COBERTURA ? 'FALLA_AGENTE' : 'BRECHA_PARCIAL';
}

const csvCell = (v) => `"${String(v ?? '').replace(/"/g, '""').replace(/\s+/g, ' ').trim()}"`;

function main() {
  const [entrada, salida = 'diagnostico.csv'] = process.argv.slice(2);
  if (!entrada) {
    console.error('Uso: node diagnostico.js resultados.json [diagnostico.csv]');
    process.exit(1);
  }

  const resultados = JSON.parse(fs.readFileSync(entrada, 'utf8')).results.results;
  const filas = resultados.map((r) => {
    const metricas = Object.fromEntries(
      (r.gradingResult?.componentResults ?? []).map((c) => [c.assertion?.metric, c]),
    );
    const diagnostico = clasificar(r);
    return {
      id: r.vars?.id,
      categoria: r.vars?.categoria,
      debe_responder: r.vars?.debe_responder,
      respondio: r.response?.metadata?.puedeResponder ? 'si' : 'no',
      diagnostico,
      cobertura: metricas.cobertura?.score?.toFixed?.(2) ?? '',
      fidelidad: metricas.fiel?.score?.toFixed?.(2) ?? '',
      correcta: metricas.correcta ? (metricas.correcta.pass ? 'si' : 'no') : '',
      accion: DIAGNOSTICOS[diagnostico].accion,
      ticket: r.vars?.ticket,
      respuesta_referencia: r.vars?.respuesta_referencia,
      respuesta_agente: r.response?.output ?? r.error,
      motivo_cobertura: metricas.cobertura?.reason ?? '',
      motivo_correccion: metricas.correcta?.reason ?? '',
      motivo_fidelidad: metricas.fiel?.reason ?? '',
    };
  });

  const columnas = Object.keys(filas[0] ?? { id: '' });
  fs.writeFileSync(
    salida,
    '﻿' + [columnas.join(','), ...filas.map((f) => columnas.map((c) => csvCell(f[c])).join(','))].join('\n') + '\n',
  );

  // Resumen
  const n1 = filas.filter((f) => /^s/i.test(f.debe_responder ?? ''));
  const cubiertos = n1.filter((f) => f.diagnostico === 'CUBIERTO').length;
  console.log(`\nTickets evaluados: ${filas.length} (${n1.length} de primer nivel)`);
  console.log(
    `Capacidad N1: ${cubiertos}/${n1.length} resueltos correctamente con la base actual` +
      (n1.length ? ` (${Math.round((cubiertos / n1.length) * 100)}%)` : ''),
  );

  console.log('\nPor diagnóstico:');
  for (const [clave, d] of Object.entries(DIAGNOSTICOS)) {
    const ids = filas.filter((f) => f.diagnostico === clave).map((f) => f.id);
    if (ids.length) console.log(`  ${d.titulo.padEnd(26)} ${String(ids.length).padStart(3)}  ${ids.join(', ')}`);
  }

  console.log('\nPor categoría (cubiertos / tickets N1):');
  const categorias = [...new Set(n1.map((f) => f.categoria))].sort();
  for (const c of categorias) {
    const deCategoria = n1.filter((f) => f.categoria === c);
    const ok = deCategoria.filter((f) => f.diagnostico === 'CUBIERTO').length;
    console.log(`  ${String(c).padEnd(26)} ${ok}/${deCategoria.length}`);
  }

  console.log(`\nDetalle y acciones sugeridas en ${salida}\n`);
}

if (require.main === module) main();

module.exports = { clasificar };
