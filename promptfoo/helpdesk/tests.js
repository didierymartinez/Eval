// Arma los casos de promptfoo a partir de dataset/tickets.csv o de HELPDESK_DATASET, que
// puede ser otra ruta o la URL de exportación CSV de un Google Sheet compartido:
//   https://docs.google.com/spreadsheets/d/<SHEET_ID>/export?format=csv&gid=<GID>
// Cada ticket lleva evaluadores distintos según si debe resolverse en primer nivel:
//   decision    ¿respondió o escaló, como se esperaba?             (todos)
//   fiel        ¿la respuesta se apoya en lo recuperado del RAG?    (todos)
//   cobertura   ¿lo recuperado contiene la respuesta de referencia? (debe_responder = si)
//   correcta    ¿la respuesta resuelve el ticket como la referencia? (debe_responder = si)
// Un ticket pasa si tomó la decisión correcta y, si era de N1, respondió bien.
// "fiel" y "cobertura" van con weight 0: se miden (puntaje 0-1) pero no hacen fallar
// el caso; diagnostico.js las usa para explicar por qué falló. Así un escalamiento
// correcto no se marca como fallo por "no estar respaldado en el contexto".
// Los nombres de métrica los usa diagnostico.js para clasificar cada ticket.

const fs = require('fs');
const path = require('path');

const DATASET = process.env.HELPDESK_DATASET || path.join(__dirname, '..', '..', 'dataset', 'tickets.csv');
const UMBRAL_COBERTURA = Number(process.env.UMBRAL_COBERTURA || 0.6);
const UMBRAL_FIDELIDAD = Number(process.env.UMBRAL_FIDELIDAD || 0.7);

// Contexto recuperado por el agente (ver transform.js). Si no recuperó nada se manda
// un texto explícito, porque si no promptfoo usaría el prompt como contexto.
const CONTEXTO =
  'context.metadata?.contextos?.length ? context.metadata.contextos : "EL AGENTE NO RECUPERÓ NINGÚN DOCUMENTO"';

// CSV con comillas dobles (RFC 4180), suficiente para lo que exporta Google Sheets o Excel.
function parseCsv(text) {
  const rows = [];
  let row = [];
  let field = '';
  let quoted = false;
  for (let i = 0; i < text.length; i++) {
    const c = text[i];
    if (quoted) {
      if (c === '"' && text[i + 1] === '"') {
        field += '"';
        i++;
      } else if (c === '"') {
        quoted = false;
      } else {
        field += c;
      }
    } else if (c === '"') {
      quoted = true;
    } else if (c === ',') {
      row.push(field);
      field = '';
    } else if (c === '\n' || c === '\r') {
      if (c === '\r' && text[i + 1] === '\n') i++;
      row.push(field);
      rows.push(row);
      row = [];
      field = '';
    } else {
      field += c;
    }
  }
  if (field || row.length) {
    row.push(field);
    rows.push(row);
  }

  const [header, ...data] = rows.filter((r) => r.some((v) => v.trim()));
  const keys = header.map((h) => h.replace(/^﻿/, '').trim());
  return data.map((r) => Object.fromEntries(keys.map((k, i) => [k, (r[i] ?? '').trim()])));
}

async function leerDataset() {
  if (!/^https?:\/\//.test(DATASET)) return fs.readFileSync(DATASET, 'utf8');
  const res = await fetch(DATASET);
  if (!res.ok) throw new Error(`No se pudo leer el dataset (${res.status}): ${DATASET}`);
  return res.text();
}

module.exports = async function generarCasos() {
  const tickets = parseCsv(await leerDataset());

  return tickets.map((t) => {
    const debeResponder = /^s/i.test(t.debe_responder);
    const assert = [
      { type: 'javascript', value: 'file://decision.js', metric: 'decision' },
      {
        type: 'context-faithfulness',
        contextTransform: CONTEXTO,
        threshold: UMBRAL_FIDELIDAD,
        metric: 'fiel',
        weight: 0,
      },
    ];

    if (debeResponder && t.respuesta_referencia) {
      assert.push(
        {
          type: 'context-recall',
          value: t.respuesta_referencia,
          contextTransform: CONTEXTO,
          threshold: UMBRAL_COBERTURA,
          metric: 'cobertura',
          weight: 0,
        },
        {
          type: 'llm-rubric',
          value:
            'La respuesta resuelve el ticket del cliente con la misma información esencial que esta respuesta ' +
            `de referencia de un agente humano de primer nivel: "${t.respuesta_referencia}". ` +
            'No hace falta que coincida textualmente, pero no debe omitir pasos clave ni contradecirla.',
          metric: 'correcta',
        },
      );
    }

    return {
      description: `${t.id} · ${t.categoria}`,
      vars: { ...t, query: t.ticket },
      assert,
      metadata: { categoria: t.categoria },
    };
  });
};
