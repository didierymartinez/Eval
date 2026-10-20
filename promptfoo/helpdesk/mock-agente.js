// Webhook de mentira que imita a un agente con RAG que devuelve sus pasos intermedios,
// para probar el eval sin tu agente. Base de conocimiento mínima y búsqueda por palabras.
// A la base le falta a propósito cómo crear usuarios (HD-005) y tiene datos de empresa
// que hacen que el agente responda un caso que debía escalar (HD-006).
//
// Uso: node mock-agente.js   ->  AGENT_WEBHOOK_URL=http://localhost:5678/webhook/helpdesk

const http = require('http');

const BASE = [
  { source: 'manual-acceso.pdf', text: 'Después de 5 intentos fallidos el usuario se bloquea durante 30 minutos. Para desbloquearlo de inmediato use la opción Olvidé mi contraseña en la pantalla de ingreso.' },
  { source: 'manual-facturacion.pdf', text: 'Las facturas electrónicas enviadas a la DIAN no se pueden anular. Para corregirlas emita una nota crédito desde Ventas > Notas crédito referenciando la factura original.' },
  { source: 'manual-inventario.pdf', text: 'Los reportes de inventario se generan en Inventario > Reportes. Una vez generado, use el botón Exportar y elija Excel o PDF.' },
  { source: 'manual-nomina.pdf', text: 'Los recargos se configuran en Nómina > Parámetros > Recargos. La hora extra nocturna tiene por defecto un recargo de 75%.' },
  { source: 'manual-empresa.pdf', text: 'Los datos de la empresa como razón social, NIT y dirección se ven en Administración > Empresa.' },
];

const VACIAS = new Set(['que', 'los', 'las', 'del', 'por', 'con', 'una', 'para', 'como', 'esta', 'use']);

// Palabras sin tildes, recortadas a 6 letras para que "bloqueado" encuentre "bloquea".
const palabras = (s) =>
  (s.toLowerCase().normalize('NFD').replace(/[̀-ͯ]/g, '').match(/[a-z0-9]{3,}/g) ?? [])
    .filter((w) => !VACIAS.has(w))
    .map((w) => w.slice(0, 6));

function buscar(consulta, k = 2) {
  const q = new Set(palabras(consulta));
  return BASE.map((d) => ({ ...d, score: palabras(d.text).filter((w) => q.has(w)).length }))
    .filter((d) => d.score > 0)
    .sort((a, b) => b.score - a.score)
    .slice(0, k);
}

function agente(mensaje) {
  const docs = buscar(mensaje);
  const steps = [
    {
      action: { tool: 'base_conocimiento', toolInput: { input: mensaje } },
      observation: JSON.stringify(
        docs.map((d) => ({ type: 'text', text: JSON.stringify({ pageContent: d.text, metadata: { source: d.source } }) })),
      ),
    },
  ];

  if (/error \d{3}|falla/i.test(mensaje) || docs.length === 0 || docs[0].score < 2) {
    return {
      output: { puede_responder: false, respuesta: 'No tengo información suficiente para resolverlo; lo escalo a segundo nivel.' },
      intermediateSteps: steps,
    };
  }
  return { output: { puede_responder: true, respuesta: docs[0].text }, intermediateSteps: steps };
}

const port = Number(process.env.PORT || 5678);
http
  .createServer((req, res) => {
    if (req.method !== 'POST' || !req.url.startsWith('/webhook/helpdesk')) {
      res.writeHead(404).end();
      return;
    }
    let body = '';
    req.on('data', (c) => (body += c));
    req.on('end', () => {
      const { mensaje = '' } = JSON.parse(body || '{}');
      res.writeHead(200, { 'Content-Type': 'application/json' });
      res.end(JSON.stringify(agente(mensaje)));
    });
  })
  .listen(port, () => console.log(`Agente simulado en http://localhost:${port}/webhook/helpdesk`));
