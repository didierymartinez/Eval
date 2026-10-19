'use strict';

// ---------- Utilidades ----------

const $app = document.getElementById('app');
let cleanups = [];
let catalog = { checkTypes: [], estado: { juezConfigurado: false } };

// Crea elementos sin innerHTML: todo el texto que viene de datos va como texto, nunca como HTML.
function h(tag, attrs, ...children) {
  const el = document.createElement(tag);
  for (const [key, value] of Object.entries(attrs || {})) {
    if (value === null || value === undefined || value === false) continue;
    if (key.startsWith('on')) el.addEventListener(key.slice(2), value);
    else if (key === 'class') el.className = value;
    else if (key === 'value') el.value = value;
    else if (key === 'checked') el.checked = !!value;
    else if (key === 'style' && typeof value === 'object') Object.assign(el.style, value);
    else el.setAttribute(key, value === true ? '' : value);
  }
  append(el, children);
  return el;
}

function append(el, children) {
  for (const child of children.flat(Infinity)) {
    if (child === null || child === undefined || child === false) continue;
    el.append(child instanceof Node ? child : document.createTextNode(String(child)));
  }
  return el;
}

async function api(path, { method = 'GET', body, form } = {}) {
  const options = { method, headers: {} };
  if (form) options.body = form;
  else if (body !== undefined) {
    options.body = JSON.stringify(body);
    options.headers['Content-Type'] = 'application/json';
  }
  const res = await fetch(`/api${path}`, options);
  const text = await res.text();
  let data = null;
  try { data = text ? JSON.parse(text) : null; } catch { data = text; }
  if (!res.ok) {
    const message = (data && (data.error || data.title || data.detail)) || `Error ${res.status}`;
    throw new Error(message);
  }
  return data;
}

let toastTimer;
function toast(message, isError = false) {
  const el = document.getElementById('toast');
  el.textContent = message;
  el.className = `show${isError ? ' error' : ''}`;
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => (el.className = ''), isError ? 6000 : 3000);
}

const fmtDate = (iso) => (iso ? new Date(iso).toLocaleString('es', { dateStyle: 'medium', timeStyle: 'short' }) : '—');
const pct = (x) => (x === null || x === undefined ? '—' : `${Math.round(x * 100)}%`);
const query = () => new URLSearchParams(location.hash.split('?')[1] || '');
const go = (hash) => { location.hash = hash; };
const checkInfo = (type) => catalog.checkTypes.find((t) => t.type === type);

// replaceChildren no aplana listas ni ignora null; esto sí.
function replace(el, ...children) {
  el.replaceChildren();
  return append(el, children);
}

function render(...nodes) {
  replace($app, nodes);
  $app.focus({ preventScroll: true });
}

function pageHead(title, subtitle, ...actions) {
  return h('div', { class: 'page-head' },
    h('div', null, h('h1', null, title), subtitle ? h('p', { class: 'subtitle' }, subtitle) : null),
    actions.length ? h('div', { class: 'actions' }, actions) : null);
}

function field(labelText, control, help) {
  return h('div', { class: 'field' }, h('label', null, labelText), control, help ? h('div', { class: 'help' }, help) : null);
}

function emptyRow(colspan, text) {
  return h('tr', null, h('td', { colspan, class: 'empty' }, text));
}

function runStatusBadge(status) {
  const map = {
    Queued: ['info', 'En cola'], Running: ['info', 'Ejecutando…'], Completed: ['ok', 'Terminada'],
    Failed: ['bad', 'Falló'], Cancelled: ['skip', 'Cancelada'],
  };
  const [cls, text] = map[status] || ['skip', status];
  return h('span', { class: `badge ${cls}` }, text);
}

function outcomeBadge(outcome) {
  const map = { Passed: ['ok', 'Aprobado'], Failed: ['bad', 'Fallido'], Error: ['warn', 'Error'] };
  const [cls, text] = map[outcome] || ['skip', outcome];
  return h('span', { class: `badge ${cls}` }, text);
}

function checkDot(check) {
  const map = { Passed: ['ok', '✓', 'Pasó'], Failed: ['bad', '✕', 'Falló'], Skipped: ['skip', '–', 'Omitida'], Error: ['warn', '!', 'Error'] };
  const [cls, icon, text] = map[check.status] || ['skip', '?', check.status];
  return h('span', { class: `dot ${cls}`, title: `${check.label}: ${text}${check.display ? ` (${check.display})` : ''}. ${check.reason}` }, icon);
}

function passRateBadge(run) {
  if (!run) return h('span', { class: 'muted' }, 'Sin corridas');
  if (run.status === 'Queued' || run.status === 'Running') return runStatusBadge(run.status);
  const rate = run.passRate;
  const cls = rate === null ? 'skip' : rate >= 0.9 ? 'ok' : rate >= 0.6 ? 'warn' : 'bad';
  return h('span', { class: `badge ${cls}` }, `${pct(rate)} aprobados`);
}

async function confirmAndDelete(message, path, after) {
  if (!confirm(message)) return;
  try {
    await api(path, { method: 'DELETE' });
    toast('Eliminado');
    go(after);
  } catch (e) {
    toast(e.message, true);
  }
}

// ---------- Inicio ----------

async function pageHome() {
  const [agents, datasets, evaluations, runs] = await Promise.all([
    api('/agentes'), api('/datasets'), api('/evaluaciones'), api('/corridas?limite=8'),
  ]);
  const step = (n, done, title, text, href, button) =>
    h('div', { class: `card step${done ? ' done' : ''}` },
      h('span', { class: 'num' }, done ? '✓' : n),
      h('h3', null, title),
      h('p', null, text),
      h('div', null, h('a', { class: 'button', href }, button)));

  const demo = evaluations.find((e) => e.name.startsWith('Demo'));
  render(
    pageHead('Evaluaciones de agentes', 'Comprueba con casos reales si tu agente o tu prompt responde lo que esperas.'),
    demo && !demo.lastRun
      ? h('div', { class: 'notice info' },
          h('strong', null, '¿Primera vez? '),
          'Hay una evaluación de ejemplo lista con un agente de helpdesk simulado. ',
          h('button', { class: 'primary', onclick: () => runEvaluation(demo.id, { askNote: false }) }, 'Ejecutar el ejemplo'))
      : null,
    h('div', { class: 'steps' },
      step(1, agents.length > 0, 'Conecta tu agente', 'Dónde está (URL) y cómo se le habla. Sirve un webhook de n8n, un endpoint propio o la API de un modelo.', '#/agentes', agents.length ? `${agents.length} agente(s)` : 'Conectar agente'),
      step(2, datasets.length > 0, 'Sube los casos de prueba', 'Un Excel, CSV o Google Sheet con la entrada y lo que esperas en cada caso.', '#/casos', datasets.length ? `${datasets.length} grupo(s) de casos` : 'Subir casos'),
      step(3, evaluations.length > 0, 'Define qué comparar', 'Elige qué revisar en cada respuesta: que contenga algo, que sea correcta según un juez IA, que use los documentos…', '#/evaluaciones', evaluations.length ? `${evaluations.length} evaluación(es)` : 'Crear evaluación'),
      step(4, runs.length > 0, 'Ejecuta y revisa', 'Mira qué casos pasaron, cuáles fallaron y por qué. Descarga el resultado en Excel.', '#/resultados', runs.length ? 'Ver resultados' : 'Aún no hay resultados')),
    h('h2', null, 'Últimas corridas'),
    runsTable(runs));
}

function runsTable(runs) {
  return h('div', { class: 'table-wrap' }, h('table', null,
    h('thead', null, h('tr', null, h('th', null, 'Evaluación'), h('th', null, 'Agente'), h('th', null, 'Fecha'), h('th', null, 'Estado'), h('th', null, 'Resultado'))),
    h('tbody', null, runs.length ? runs.map((r) =>
      h('tr', { class: 'clickable', onclick: () => go(`#/resultados/${r.id}`) },
        h('td', null, r.evaluationName),
        h('td', { class: 'muted' }, r.agentName),
        h('td', { class: 'muted' }, fmtDate(r.createdAt)),
        h('td', null, runStatusBadge(r.status)),
        h('td', null, r.status === 'Completed' || r.completed ? `${r.passed} de ${r.completed} aprobados` : '—'))) : emptyRow(5, 'Todavía no se ha ejecutado ninguna evaluación.'))));
}

// ---------- Agentes ----------

async function pageAgents() {
  const agents = await api('/agentes');
  render(
    pageHead('Agentes', 'Dónde está cada agente o prompt que quieres evaluar.', h('a', { class: 'button primary', href: '#/agentes/nuevo' }, '+ Nuevo agente')),
    h('div', { class: 'table-wrap' }, h('table', null,
      h('thead', null, h('tr', null, h('th', null, 'Nombre'), h('th', null, 'URL'), h('th', null, 'Respuesta en'))),
      h('tbody', null, agents.length ? agents.map((a) =>
        h('tr', { class: 'clickable', onclick: () => go(`#/agentes/${a.id}`) },
          h('td', null, h('strong', null, a.name)),
          h('td', { class: 'mono cell-clip' }, `${a.method} ${a.url}`),
          h('td', { class: 'mono' }, a.responsePath || '(todo el cuerpo)'))) : emptyRow(3, 'Aún no hay agentes. Crea el primero.')))));
}

async function pageAgentForm(id) {
  const templates = id ? [] : await api('/plantillas-agente');
  const a = id ? await api(`/agentes/${id}`) : {
    name: '', url: '', method: 'POST', headers: {}, bodyTemplate: '{ "input": "{{input}}" }',
    responsePath: 'output', contextPath: '', toolsPath: '', tokensPath: '', timeoutSeconds: 120,
  };
  let headers = Object.entries(a.headers || {});
  let testInput = '';
  const testResult = h('div');

  const collect = () => ({
    name: a.name, url: a.url, method: a.method,
    headers: Object.fromEntries(headers.filter(([k]) => k.trim())),
    bodyTemplate: a.bodyTemplate, responsePath: a.responsePath, contextPath: a.contextPath, toolsPath: a.toolsPath,
    tokensPath: a.tokensPath, timeoutSeconds: Number(a.timeoutSeconds) || 120,
  });

  const bind = (key, attrs = {}) => h(attrs.multiline ? 'textarea' : 'input', {
    type: attrs.multiline ? null : 'text', value: a[key] ?? '', placeholder: attrs.placeholder,
    rows: attrs.rows, oninput: (e) => (a[key] = e.target.value),
  });

  const headersEditor = () => {
    const box = h('div');
    const draw = () => replace(box,
      ...headers.map((pair, i) => h('div', { class: 'kv-row' },
        h('input', { type: 'text', value: pair[0], placeholder: 'Nombre (ej. Authorization)', oninput: (e) => (pair[0] = e.target.value) }),
        h('input', { type: 'text', value: pair[1], placeholder: 'Valor (ej. Bearer {{env:MI_TOKEN}})', oninput: (e) => (pair[1] = e.target.value) }),
        h('button', { type: 'button', class: 'link', title: 'Quitar', onclick: () => { headers.splice(i, 1); draw(); } }, 'Quitar'))),
      h('button', { type: 'button', onclick: () => { headers.push(['', '']); draw(); } }, '+ Encabezado'));
    draw();
    return box;
  };

  const runTest = async () => {
    replace(testResult, h('p', { class: 'muted' }, 'Llamando al agente…'));
    try {
      const r = await api('/agentes/probar', { method: 'POST', body: { agent: collect(), input: testInput } });
      replace(testResult,
        h('div', { class: `notice ${r.ok ? 'ok' : 'bad'}` },
          h('strong', null, r.ok ? `Conectó en ${r.latencyMs} ms` : 'No funcionó'),
          r.error ? h('div', null, r.error) : null),
        r.ok ? h('div', null,
          h('h3', null, 'Respuesta que se va a evaluar'),
          r.output ? h('div', { class: 'text-block' }, r.output) : h('div', { class: 'notice warn' }, 'La ruta de la respuesta no encontró nada. Revisa "Dónde viene la respuesta" con el JSON completo de abajo.'),
          a.contextPath ? h('p', null, h('strong', null, `Documentos recuperados: ${r.context.length}`), r.context.length ? null : h('span', { class: 'muted' }, ' (la ruta no encontró documentos)')) : null,
          r.context.length ? h('details', null, h('summary', null, 'Ver documentos'), r.context.map((c) => h('pre', null, c))) : null,
          a.toolsPath ? h('p', null, h('strong', null, 'Herramientas: '), r.tools.length ? r.tools.join(', ') : h('span', { class: 'muted' }, 'ninguna')) : null,
          a.tokensPath ? h('p', null, h('strong', null, 'Tokens: '), r.tokens ?? h('span', { class: 'muted' }, 'la ruta no encontró tokens')) : null) : null,
        h('details', null, h('summary', null, 'Lo que se envió'), h('pre', null, r.requestBody || '(sin cuerpo)')),
        r.raw ? h('details', { open: !r.ok || !r.output }, h('summary', null, 'Respuesta completa del agente'), h('pre', null, pretty(r.raw))) : null);
    } catch (e) {
      replace(testResult, h('div', { class: 'notice bad' }, e.message));
    }
  };

  const save = async () => {
    try {
      const saved = id
        ? await api(`/agentes/${id}`, { method: 'PUT', body: collect() })
        : await api('/agentes', { method: 'POST', body: collect() });
      toast('Agente guardado');
      go(`#/agentes/${saved.id}`);
      if (id) route();
    } catch (e) {
      toast(e.message, true);
    }
  };

  const form = () => h('div', null,
    h('div', { class: 'card' },
      field('Nombre', bind('name', { placeholder: 'Ej. Agente de helpdesk (producción)' })),
      h('div', { class: 'grid-3' },
        field('URL', bind('url', { placeholder: 'https://…' }), 'Puede llevar {{columna}} si cambia por caso.'),
        field('Método', h('select', { onchange: (e) => (a.method = e.target.value) },
          ['POST', 'GET', 'PUT', 'PATCH'].map((m) => h('option', { value: m, selected: a.method === m }, m)))),
        field('Tiempo máximo (seg.)', h('input', { type: 'number', min: 1, max: 1800, value: a.timeoutSeconds, oninput: (e) => (a.timeoutSeconds = e.target.value) }))),
      field('Encabezados', headersEditor(), h('span', null, 'Para claves o tokens usa ', h('code', null, '{{env:NOMBRE}}'), ': se lee de una variable de entorno del servidor y no queda guardado aquí.')),
      field('Cuerpo (JSON)', bind('bodyTemplate', { multiline: true, rows: 6 }),
        h('span', null, h('code', null, '{{input}}'), ' se reemplaza por la entrada de cada caso y ', h('code', null, '{{nombre_columna}}'), ' por cualquier otra columna de tus casos.'))),
    h('div', { class: 'card' },
      h('h3', null, 'Qué sacar de la respuesta'),
      h('p', { class: 'muted small' }, 'Rutas dentro del JSON que devuelve el agente. Ejemplos: ', h('code', null, 'output'), ', ', h('code', null, 'output.respuesta'), ', ', h('code', null, 'choices[0].message.content'), ', ', h('code', null, 'intermediateSteps[*].observation'), '. Usa "Probar" para ver el JSON completo.'),
      field('Dónde viene la respuesta', bind('responsePath', { placeholder: 'output (vacío = toda la respuesta)' })),
      h('div', { class: 'grid-2' },
        field('Dónde vienen los documentos recuperados (opcional, RAG)', bind('contextPath', { placeholder: 'intermediateSteps[*].observation' }), 'Necesario para las comparaciones de base de conocimiento.'),
        field('Dónde vienen las herramientas usadas (opcional)', bind('toolsPath', { placeholder: 'intermediateSteps[*].action.tool' }), 'Necesario para "Usó las herramientas".')),
      field('Dónde vienen los tokens consumidos (opcional)', bind('tokensPath', { placeholder: 'usage.total_tokens' }),
        'Varias rutas separadas por coma se suman (ej. usage.input_tokens, usage.output_tokens). El webhook de n8n no los devuelve por defecto.')),
    h('div', { class: 'card' },
      h('h3', null, 'Probar la conexión'),
      field('Escribe una entrada de ejemplo', h('textarea', { rows: 2, style: { fontFamily: 'inherit', fontSize: '15px', minHeight: '0' }, placeholder: 'Ej. No puedo entrar, me dice que mi usuario está bloqueado', oninput: (e) => (testInput = e.target.value) })),
      h('button', { type: 'button', onclick: runTest }, 'Probar'),
      testResult),
    h('div', { class: 'actions' },
      h('button', { class: 'primary', onclick: save }, 'Guardar agente'),
      h('a', { class: 'button', href: '#/agentes' }, 'Volver'),
      id ? h('button', { class: 'danger', onclick: () => confirmAndDelete('¿Eliminar este agente?', `/agentes/${id}`, '#/agentes') }, 'Eliminar') : null));

  const container = h('div');
  const draw = () => replace(container, form());
  draw();

  render(
    pageHead(id ? a.name : 'Nuevo agente', id ? 'Edita cómo se conecta la plataforma con este agente.' : 'Empieza desde una plantilla o llena los datos a mano.'),
    templates.length ? h('div', null,
      h('h3', null, 'Plantillas'),
      h('div', { class: 'templates' }, templates.map((t) => h('button', {
        class: 'template', type: 'button',
        onclick: () => {
          Object.assign(a, { ...t.agent, contextPath: t.agent.contextPath || '', toolsPath: t.agent.toolsPath || '', tokensPath: t.agent.tokensPath || '' });
          headers = Object.entries(t.agent.headers || {});
          draw();
          toast(`Plantilla "${t.name}" aplicada: ajusta la URL`);
        },
      }, h('strong', null, t.name), h('span', null, t.description))))) : null,
    container);
}

function pretty(text) {
  try { return JSON.stringify(JSON.parse(text), null, 2); } catch { return text; }
}

// ---------- Casos de prueba (datasets) ----------

async function pageDatasets() {
  const datasets = await api('/datasets');
  render(
    pageHead('Casos de prueba', 'Tablas con la entrada de cada caso y lo que esperas que responda el agente.',
      h('a', { class: 'button', href: '/api/datasets/plantilla.csv' }, 'Descargar plantilla'),
      h('a', { class: 'button primary', href: '#/casos/nuevo' }, '+ Agregar casos')),
    h('div', { class: 'table-wrap' }, h('table', null,
      h('thead', null, h('tr', null, h('th', null, 'Nombre'), h('th', null, 'Casos'), h('th', null, 'Columnas'), h('th', null, 'Origen'), h('th', null, 'Actualizado'))),
      h('tbody', null, datasets.length ? datasets.map((d) =>
        h('tr', { class: 'clickable', onclick: () => go(`#/casos/${d.id}`) },
          h('td', null, h('strong', null, d.name)),
          h('td', null, d.rowCount),
          h('td', null, h('div', { class: 'chips' }, d.columns.slice(0, 6).map((c) => h('span', { class: 'chip' }, c)), d.columns.length > 6 ? h('span', { class: 'chip' }, `+${d.columns.length - 6}`) : null)),
          h('td', { class: 'muted small' }, d.sourceUrl ? 'Enlace' : d.sourceFileName || 'Archivo'),
          h('td', { class: 'muted small' }, fmtDate(d.updatedAt)))) : emptyRow(5, 'Aún no hay casos. Sube un Excel, CSV o enlaza un Google Sheet.')))));
}

function pageDatasetNew() {
  let tab = 'archivo';
  const body = h('div');
  const draw = () => {
    const tabs = h('div', { class: 'tabs' },
      h('button', { class: tab === 'archivo' ? 'active' : '', onclick: () => { tab = 'archivo'; draw(); } }, 'Subir archivo (Excel o CSV)'),
      h('button', { class: tab === 'enlace' ? 'active' : '', onclick: () => { tab = 'enlace'; draw(); } }, 'Enlace de Google Sheets'),
      h('button', { class: tab === 'generar' ? 'active' : '', onclick: () => { tab = 'generar'; draw(); } }, 'Generar con IA'));
    const name = h('input', { type: 'text', placeholder: 'Ej. Tickets de soporte de octubre' });

    if (tab === 'archivo') {
      const file = h('input', { type: 'file', accept: '.csv,.xlsx,.xlsm,.txt' });
      replace(body, tabs,
        field('Nombre', name, 'Si lo dejas vacío se usa el nombre del archivo.'),
        field('Archivo', file, 'La primera fila debe tener los nombres de las columnas. En Excel se usa la primera hoja.'),
        h('button', {
          class: 'primary',
          onclick: async () => {
            if (!file.files.length) return toast('Elige un archivo', true);
            const form = new FormData();
            form.append('nombre', name.value);
            form.append('archivo', file.files[0]);
            try {
              const r = await api('/datasets/archivo', { method: 'POST', form });
              toast('Casos cargados');
              go(`#/casos/${r.id}`);
            } catch (e) { toast(e.message, true); }
          },
        }, 'Cargar casos'));
    } else if (tab === 'generar') {
      const area = (placeholder, rows) => h('textarea', { rows, placeholder, style: { fontFamily: 'inherit', fontSize: '15px' } });
      const description = area('Ej.: Agente de soporte de primer nivel de un software contable. Responde dudas de facturación electrónica, nómina e inventario con la base de conocimiento. Si es un error técnico o requiere cambiar datos de la empresa, crea un ticket para segundo nivel.', 4);
      const tools = area('Una por línea. Ej.:\nbuscar_base_conocimiento\ncrear_ticket\nenviar_correo', 3);
      const knowledge = area('Opcional: pega preguntas frecuentes, políticas o partes del manual para que los casos usen datos reales.', 5);
      const count = h('input', { type: 'number', min: 3, max: 50, value: 20 });
      const button = h('button', {
        class: 'primary',
        disabled: !catalog.estado.juezConfigurado,
        onclick: async () => {
          button.disabled = true;
          button.textContent = 'Generando… (puede tardar un minuto)';
          try {
            const r = await api('/datasets/generar', { method: 'POST', body: { name: name.value, description: description.value, tools: tools.value, knowledge: knowledge.value, count: Number(count.value) } });
            toast(`Se generaron ${r.rowCount} casos. Revísalos antes de usarlos.`);
            go(`#/casos/${r.id}`);
          } catch (e) {
            toast(e.message, true);
            button.disabled = false;
            button.textContent = 'Generar casos';
          }
        },
      }, 'Generar casos');
      replace(body, tabs,
        catalog.estado.juezConfigurado
          ? h('div', { class: 'notice info small' }, 'El modelo arma un primer borrador con preguntas frecuentes, casos límite y casos que el agente debe escalar o rechazar. Revísalo: descárgalo en Excel, corrige lo que no esté bien y vuelve a subirlo (o pásalo a un Google Sheet).')
          : h('div', { class: 'notice warn' }, 'Para generar casos con IA hay que configurar el juez IA en el servidor (ver README, sección "Juez IA").'),
        field('Nombre', name),
        field('Qué hace el agente', description),
        h('div', { class: 'grid-2' },
          field('Herramientas que puede usar', tools),
          field('Cantidad de casos', count, 'Entre 3 y 50.')),
        field('Información que tiene disponible', knowledge),
        button);
    } else {
      const url = h('input', { type: 'url', placeholder: 'https://docs.google.com/spreadsheets/d/…' });
      replace(body, tabs,
        h('div', { class: 'notice info small' }, 'En Google Sheets: Compartir → Acceso general → "Cualquier persona con el enlace" (lector). Copia el enlace de la pestaña que tiene los casos.'),
        field('Nombre', name),
        field('Enlace', url, 'También sirve el enlace a cualquier CSV publicado en la web. Podrás volver a cargarlo cuando cambie la hoja.'),
        h('button', {
          class: 'primary',
          onclick: async () => {
            try {
              const r = await api('/datasets/enlace', { method: 'POST', body: { name: name.value, url: url.value } });
              toast('Casos cargados');
              go(`#/casos/${r.id}`);
            } catch (e) { toast(e.message, true); }
          },
        }, 'Cargar casos'));
    }
  };
  draw();
  render(
    pageHead('Agregar casos de prueba', 'Una fila por caso. Una columna con la entrada, y las columnas que quieras con lo esperado (respuesta, textos que debe contener, criterio…).',
      h('a', { class: 'button', href: '/api/datasets/plantilla.csv' }, 'Descargar plantilla')),
    h('div', { class: 'card' }, body));
}

async function pageDataset(id) {
  const d = await api(`/datasets/${id}`);
  const replace = h('input', {
    type: 'file', accept: '.csv,.xlsx,.xlsm,.txt', style: { display: 'none' },
    onchange: async (e) => {
      const form = new FormData();
      form.append('archivo', e.target.files[0]);
      try {
        await api(`/datasets/${id}/archivo`, { method: 'PUT', form });
        toast('Casos actualizados');
        route();
      } catch (err) { toast(err.message, true); }
    },
  });
  const preview = d.rows.slice(0, 100);
  render(
    pageHead(d.name, `${d.rows.length} casos · ${d.sourceUrl ? 'desde enlace' : d.sourceFileName || 'archivo'} · actualizado ${fmtDate(d.updatedAt)}`,
      h('a', { class: 'button primary', href: `#/evaluaciones/nueva?dataset=${id}` }, 'Crear evaluación con estos casos'),
      h('a', { class: 'button', href: `/api/datasets/${id}/excel` }, 'Descargar Excel'),
      d.sourceUrl ? h('button', {
        onclick: async () => {
          try { const r = await api(`/datasets/${id}/recargar`, { method: 'POST' }); toast(`Recargado: ${r.rowCount} casos`); route(); } catch (e) { toast(e.message, true); }
        },
      }, 'Volver a cargar desde el enlace') : h('button', { onclick: () => replace.click() }, 'Reemplazar archivo'),
      replace,
      h('button', {
        onclick: async () => {
          const name = prompt('Nuevo nombre', d.name);
          if (!name) return;
          try { await api(`/datasets/${id}`, { method: 'PUT', body: { name } }); route(); } catch (e) { toast(e.message, true); }
        },
      }, 'Renombrar'),
      h('button', { class: 'danger', onclick: () => confirmAndDelete('¿Eliminar estos casos?', `/datasets/${id}`, '#/casos') }, 'Eliminar')),
    d.sourceUrl ? h('p', { class: 'small muted' }, 'Enlace: ', h('a', { href: d.sourceUrl, target: '_blank', rel: 'noopener' }, d.sourceUrl)) : null,
    h('div', { class: 'table-wrap' }, h('table', null,
      h('thead', null, h('tr', null, h('th', null, '#'), d.columns.map((c) => h('th', null, c)))),
      h('tbody', null, preview.map((row, i) => h('tr', null, h('td', { class: 'muted' }, i + 1), d.columns.map((c) => h('td', { class: 'cell-clip', title: row[c] }, row[c]))))))),
    d.rows.length > preview.length ? h('p', { class: 'muted small' }, `Se muestran los primeros ${preview.length} de ${d.rows.length} casos.`) : null);
}

// ---------- Evaluaciones ----------

// askNote: pregunta qué cambió antes de ejecutar (para comparar el antes y el después).
async function runEvaluation(id, { askNote = true, note } = {}) {
  if (note === undefined && askNote) {
    note = prompt('¿Qué cambiaste desde la última corrida? (opcional)\nEj.: "prompt v2 con árbol de decisión", "temperatura 0.2", "agregué el manual de nómina"', '');
    if (note === null) return;
  }
  try {
    const r = await api(`/evaluaciones/${id}/ejecutar`, { method: 'POST', body: { note: note || null } });
    go(`#/resultados/${r.runId}`);
  } catch (e) {
    toast(e.message, true);
  }
}

async function pageEvaluations() {
  const evaluations = await api('/evaluaciones');
  render(
    pageHead('Evaluaciones', 'Qué agente se prueba, con qué casos y qué se compara en cada respuesta.', h('a', { class: 'button primary', href: '#/evaluaciones/nueva' }, '+ Nueva evaluación')),
    h('div', { class: 'table-wrap' }, h('table', null,
      h('thead', null, h('tr', null, h('th', null, 'Nombre'), h('th', null, 'Agente'), h('th', null, 'Casos'), h('th', null, 'Última corrida'), h('th', null, ''))),
      h('tbody', null, evaluations.length ? evaluations.map((e) =>
        h('tr', { class: 'clickable', onclick: () => go(`#/evaluaciones/${e.id}`) },
          h('td', null, h('strong', null, e.name), h('div', { class: 'small muted' }, `${e.checkCount} comparación(es)`)),
          h('td', { class: 'muted' }, e.agentName),
          h('td', { class: 'muted' }, e.datasetName),
          h('td', null, passRateBadge(e.lastRun), e.lastRun ? h('div', { class: 'small muted' }, fmtDate(e.lastRun.createdAt)) : null),
          h('td', null, h('button', { onclick: (ev) => { ev.stopPropagation(); runEvaluation(e.id); } }, '▶ Ejecutar')))) : emptyRow(5, 'Aún no hay evaluaciones.')))));
}

async function pageEvaluation(id) {
  const { evaluation: e, runs } = await api(`/evaluaciones/${id}`);
  const [agents, datasets] = await Promise.all([api('/agentes'), api('/datasets')]);
  const agent = agents.find((a) => a.id === e.agentId);
  const dataset = datasets.find((d) => d.id === e.datasetId);
  const completed = runs.filter((r) => r.status === 'Completed');
  const noteInput = h('input', { type: 'text', placeholder: 'Ej.: prompt v2 con árbol de decisión, temperatura 0.2, agregué el manual de nómina' });

  render(
    pageHead(e.name, null,
      h('a', { class: 'button', href: `#/evaluaciones/${id}/editar` }, 'Editar'),
      h('button', { class: 'danger', onclick: () => confirmAndDelete('¿Eliminar esta evaluación y todas sus corridas?', `/evaluaciones/${id}`, '#/evaluaciones') }, 'Eliminar')),
    h('div', { class: 'card' },
      h('div', { class: 'grid-2' },
        h('div', null, h('div', { class: 'small muted' }, 'Agente'), agent ? h('a', { href: `#/agentes/${agent.id}` }, agent.name) : '—'),
        h('div', null, h('div', { class: 'small muted' }, 'Casos'), dataset ? h('a', { href: `#/casos/${dataset.id}` }, `${dataset.name} (${dataset.rowCount})`) : '—')),
      h('h3', { style: { marginTop: '14px' } }, 'Qué se compara'),
      h('ul', null, e.checks.map((c) => {
        const info = checkInfo(c.type);
        return h('li', null, h('strong', null, c.label || info?.label || c.type),
          c.expectedColumn ? h('span', { class: 'muted' }, ` contra la columna "${c.expectedColumn}"`) : c.expectedValue ? h('span', { class: 'muted' }, ` contra "${c.expectedValue}"`) : null,
          c.blocking ? null : h('span', { class: 'badge skip', style: { marginLeft: '6px' } }, 'informativa'));
      })),
      e.repetitions > 1 ? h('p', { class: 'muted small' }, `Cada caso se ejecuta ${e.repetitions} veces.`) : null),
    h('div', { class: 'card' },
      h('label', null, '¿Qué cambiaste desde la última corrida? (opcional)'),
      h('div', { class: 'kv-row', style: { marginBottom: 0 } },
        noteInput,
        h('button', { class: 'primary', onclick: () => runEvaluation(id, { note: noteInput.value }) }, '▶ Ejecutar ahora')),
      h('div', { class: 'help' }, 'Queda en el historial para comparar el antes y el después de cada cambio.')),
    h('h2', null, 'Historial'),
    completed.length > 1 ? trendBars(completed.slice(0, 12).reverse()) : null,
    h('div', { class: 'table-wrap' }, h('table', null,
      h('thead', null, h('tr', null, h('th', null, 'Corrida'), h('th', null, 'Fecha'), h('th', null, 'Qué cambió'), h('th', null, 'Estado'), h('th', null, 'Aprobados'), h('th', null, 'Fallidos'), h('th', null, 'Errores'))),
      h('tbody', null, runs.length ? runs.map((r) =>
        h('tr', { class: 'clickable', onclick: () => go(`#/resultados/${r.id}`) },
          h('td', null, `#${r.id}`),
          h('td', { class: 'muted' }, fmtDate(r.createdAt)),
          h('td', { class: 'cell-clip', title: r.note || '' }, r.note || h('span', { class: 'muted' }, '—')),
          h('td', null, runStatusBadge(r.status)),
          h('td', null, `${r.passed} (${pct(r.passRate)})`),
          h('td', null, r.failed),
          h('td', null, r.errors))) : emptyRow(7, 'Todavía no se ha ejecutado.')))));
}

function trendBars(runs) {
  return h('div', { class: 'card' },
    h('div', { class: 'small muted', style: { marginBottom: '8px' } }, '% de casos aprobados en las últimas corridas'),
    h('div', { style: { display: 'flex', alignItems: 'flex-end', gap: '6px', height: '90px' } },
      runs.map((r) => h('a', {
        href: `#/resultados/${r.id}`, title: `#${r.id} · ${pct(r.passRate)} · ${fmtDate(r.createdAt)}${r.note ? ` · ${r.note}` : ''}`,
        style: { flex: '1', maxWidth: '48px', display: 'flex', flexDirection: 'column', alignItems: 'center', justifyContent: 'flex-end', height: '100%', textDecoration: 'none' },
      },
      h('span', { class: 'small muted' }, pct(r.passRate)),
      h('span', { style: { width: '100%', height: `${Math.max(3, (r.passRate || 0) * 64)}px`, background: 'var(--accent)', borderRadius: '4px 4px 0 0' } })))));
}

// Sugiere comparaciones según los nombres de las columnas de los casos.
function suggestChecks(columns) {
  const find = (re) => columns.find((c) => re.test(c));
  const out = [];
  const add = (type, column, extra = {}) => column && out.push({ type, expectedColumn: column, blocking: true, ...extra });
  add('correccion', find(/esperad|referencia|expected|respuesta_correcta/i));
  add('contiene', find(/debe_contener|must_contain|contiene/i));
  add('no_contiene', find(/no_debe|must_not|prohibid/i));
  add('rubrica', find(/criterio|rubric|rubrica|rúbrica/i));
  add('herramientas', find(/herramienta|tools/i));
  add('igual', find(/^(categoria_esperada|clase|etiqueta|label|decision|debe_responder)$/i));
  return out;
}

async function pageEvaluationForm(id) {
  const [agents, datasets] = await Promise.all([api('/agentes'), api('/datasets')]);
  const e = id ? (await api(`/evaluaciones/${id}`)).evaluation : {
    name: '', agentId: agents[0]?.id, datasetId: Number(query().get('dataset')) || datasets[0]?.id,
    inputColumn: '', checks: [], repetitions: 1,
  };
  if (!agents.length || !datasets.length) {
    render(pageHead('Nueva evaluación'),
      h('div', { class: 'notice warn' }, 'Para crear una evaluación primero necesitas ',
        !agents.length ? h('a', { href: '#/agentes/nuevo' }, 'un agente') : null,
        !agents.length && !datasets.length ? ' y ' : null,
        !datasets.length ? h('a', { href: '#/casos/nuevo' }, 'casos de prueba') : null, '.'));
    return;
  }

  const columnsOf = () => datasets.find((d) => d.id === Number(e.datasetId))?.columns || [];
  const guessInput = (cols) => cols.find((c) => /^(input|entrada|pregunta|ticket|mensaje|prompt)$/i.test(c)) || cols[0] || '';
  if (!e.inputColumn) e.inputColumn = guessInput(columnsOf());
  if (!id && !e.checks.length) e.checks = suggestChecks(columnsOf());

  const container = h('div');
  const families = [['Text', 'Comparar texto'], ['Behavior', 'Comportamiento'], ['AiJudge', 'Juez IA'], ['Rag', 'Base de conocimiento (RAG)']];

  const checkCard = (c, i) => {
    const info = checkInfo(c.type);
    const cols = columnsOf();
    const fixed = !c.expectedColumn && info?.needsExpected;
    return h('div', { class: 'check-card' },
      h('div', { class: 'check-head' },
        h('span', { class: 'badge info' }, i + 1),
        h('select', {
          'aria-label': 'Tipo de comparación',
          onchange: (ev) => {
            c.type = ev.target.value;
            c.threshold = null;
            // Lo esperado solo aplica a los tipos que lo usan.
            if (!checkInfo(c.type)?.needsExpected) { c.expectedColumn = null; c.expectedValue = null; }
            else if (!c.expectedColumn && !c.expectedValue) c.expectedColumn = columnsOf()[0] || null;
            draw();
          },
        }, families.map(([fam, label]) => h('optgroup', { label },
          catalog.checkTypes.filter((t) => t.family === fam).map((t) => h('option', { value: t.type, selected: t.type === c.type }, t.label))))),
        h('button', { type: 'button', class: 'link', title: 'Subir', disabled: i === 0, onclick: () => { e.checks.splice(i - 1, 0, e.checks.splice(i, 1)[0]); draw(); } }, '↑'),
        h('button', { type: 'button', class: 'link', title: 'Bajar', disabled: i === e.checks.length - 1, onclick: () => { e.checks.splice(i + 1, 0, e.checks.splice(i, 1)[0]); draw(); } }, '↓'),
        h('button', { type: 'button', class: 'link danger', onclick: () => { e.checks.splice(i, 1); draw(); } }, 'Quitar')),
      info ? h('p', { class: 'desc' }, info.description) : null,
      h('div', { class: 'check-row' },
        info?.needsExpected ? h('div', null,
          h('label', null, c.type === 'rubrica' ? 'Criterio' : 'Lo esperado viene de'),
          h('select', {
            onchange: (ev) => { c.expectedColumn = ev.target.value || null; if (c.expectedColumn) c.expectedValue = null; draw(); },
          },
          cols.map((col) => h('option', { value: col, selected: c.expectedColumn === col }, `Columna: ${col}`)),
          h('option', { value: '', selected: fixed }, c.type === 'rubrica' ? '— Escribir el mismo criterio para todos —' : '— Escribir un valor fijo —'))) : null,
        fixed ? h('div', { style: { flexBasis: '100%' } },
          h('label', null, c.type === 'rubrica' ? 'Criterio para todos los casos' : 'Valor esperado'),
          h(c.type === 'rubrica' ? 'textarea' : 'input', {
            type: 'text', value: c.expectedValue || '', rows: 2, style: c.type === 'rubrica' ? { fontFamily: 'inherit', fontSize: '15px' } : null,
            placeholder: c.type === 'rubrica' ? 'Ej. Saluda, pide el número de pedido y no promete fechas de entrega' : 'Puede usar {{columna}}',
            oninput: (ev) => (c.expectedValue = ev.target.value),
          })) : null,
        info?.defaultThreshold != null ? h('div', { style: { maxWidth: '220px' } },
          h('label', null, info.thresholdHelp),
          h('input', { type: 'number', step: 'any', value: c.threshold ?? '', placeholder: String(info.defaultThreshold), oninput: (ev) => (c.threshold = ev.target.value === '' ? null : Number(ev.target.value)) })) : null,
        h('div', null,
          h('label', { class: 'inline' }, h('input', { type: 'checkbox', checked: c.blocking !== false, onchange: (ev) => (c.blocking = ev.target.checked) }), 'Cuenta para aprobar el caso'),
          h('div', { class: 'help' }, 'Desmárcala para solo medir, sin que haga fallar el caso.'))),
      h('details', { open: !!(c.label || c.responsePath) },
        h('summary', null, 'Opciones avanzadas'),
        h('div', { class: 'grid-2', style: { marginTop: '8px' } },
          field('Nombre para mostrar', h('input', { type: 'text', value: c.label || '', placeholder: info?.label, oninput: (ev) => (c.label = ev.target.value) })),
          field('Comparar otro campo de la respuesta', h('input', { type: 'text', value: c.responsePath || '', placeholder: 'Ej. output.puede_responder', oninput: (ev) => (c.responsePath = ev.target.value) }),
            'Ruta dentro del JSON del agente. Vacío = la respuesta principal.'))));
  };

  const save = async (andRun) => {
    const body = { ...e, agentId: Number(e.agentId), datasetId: Number(e.datasetId), repetitions: Number(e.repetitions) || 1 };
    try {
      const saved = id ? await api(`/evaluaciones/${id}`, { method: 'PUT', body }) : await api('/evaluaciones', { method: 'POST', body });
      toast('Evaluación guardada');
      if (andRun) runEvaluation(saved.id, { askNote: !!id });
      else go(`#/evaluaciones/${saved.id}`);
    } catch (err) {
      toast(err.message, true);
    }
  };

  const draw = () => {
    const cols = columnsOf();
    const usesJudge = e.checks.some((c) => checkInfo(c.type)?.usesJudge);
    replace(container,
      h('div', { class: 'card' },
        field('Nombre', h('input', { type: 'text', value: e.name, placeholder: 'Ej. Helpdesk N1 · tickets de octubre', oninput: (ev) => (e.name = ev.target.value) })),
        h('div', { class: 'grid-2' },
          field('Agente', h('select', { onchange: (ev) => { e.agentId = Number(ev.target.value); } },
            agents.map((a) => h('option', { value: a.id, selected: a.id === Number(e.agentId) }, a.name)))),
          field('Casos de prueba', h('select', {
            onchange: (ev) => {
              e.datasetId = Number(ev.target.value);
              const newCols = columnsOf();
              if (!newCols.includes(e.inputColumn)) e.inputColumn = guessInput(newCols);
              if (!e.checks.length) e.checks = suggestChecks(newCols);
              draw();
            },
          }, datasets.map((d) => h('option', { value: d.id, selected: d.id === Number(e.datasetId) }, `${d.name} (${d.rowCount})`))))),
        h('div', { class: 'grid-2' },
          field('Columna que se manda al agente como entrada', h('select', { onchange: (ev) => (e.inputColumn = ev.target.value) },
            cols.map((c) => h('option', { value: c, selected: c === e.inputColumn }, c))),
            h('span', null, 'Llega al agente como ', h('code', null, '{{input}}'), '.')),
          field('Repeticiones por caso', h('input', { type: 'number', min: 1, max: 10, value: e.repetitions, oninput: (ev) => (e.repetitions = ev.target.value) }),
            'Los modelos no responden siempre igual. Con 3 ves si un caso pasa siempre o solo a veces.'))),
      h('h2', null, 'Qué comparar en cada respuesta'),
      usesJudge && !catalog.estado.juezConfigurado
        ? h('div', { class: 'notice warn' }, 'Hay comparaciones con juez IA, pero el juez no está configurado en el servidor. Esas comparaciones se van a omitir hasta que se configure (ver README, sección "Juez IA").')
        : null,
      e.checks.map(checkCard),
      e.checks.length ? null : h('div', { class: 'notice info' }, 'Agrega al menos una comparación.'),
      h('div', { class: 'actions', style: { marginBottom: '24px' } },
        h('button', { type: 'button', onclick: () => { e.checks.push({ type: 'contiene', expectedColumn: cols.find((c) => c !== e.inputColumn) || cols[0] || null, blocking: true }); draw(); } }, '+ Agregar comparación'),
        h('button', { type: 'button', class: 'link', onclick: () => { const s = suggestChecks(cols); if (!s.length) return toast('No encontré columnas reconocibles (respuesta_esperada, debe_contener, criterio…)', true); e.checks = s; draw(); } }, 'Sugerir según las columnas')),
      h('div', { class: 'actions' },
        h('button', { class: 'primary', onclick: () => save(true) }, 'Guardar y ejecutar'),
        h('button', { onclick: () => save(false) }, 'Solo guardar'),
        h('a', { class: 'button', href: id ? `#/evaluaciones/${id}` : '#/evaluaciones' }, 'Cancelar')));
  };
  draw();

  render(pageHead(id ? `Editar: ${e.name}` : 'Nueva evaluación', 'Elige el agente, los casos y qué revisar en cada respuesta.'), container);
}

// ---------- Resultados ----------

async function pageRuns() {
  const runs = await api('/corridas?limite=100');
  render(pageHead('Resultados', 'Todas las corridas, de la más reciente a la más antigua.'), runsTable(runs));
}

async function pageRun(id) {
  let filter = 'todos';
  let search = '';
  const expanded = new Set();
  let results = [];
  let data;
  let timer;

  const load = async () => {
    [data, results] = await Promise.all([api(`/corridas/${id}`), api(`/corridas/${id}/resultados`)]);
    draw();
    const active = data.run.status === 'Queued' || data.run.status === 'Running';
    if (active) timer = setTimeout(load, 1500);
  };
  cleanups.push(() => clearTimeout(timer));

  const draw = () => {
    const { run, summary } = data;
    const active = run.status === 'Queued' || run.status === 'Running';
    const delta = summary.previousPassRate != null && summary.passRate != null ? Math.round((summary.passRate - summary.previousPassRate) * 100) : null;
    const counts = {
      todos: results.length,
      fallidos: results.filter((r) => r.outcome === 'Failed').length,
      errores: results.filter((r) => r.outcome === 'Error').length,
      aprobados: results.filter((r) => r.outcome === 'Passed').length,
    };
    const term = search.trim().toLowerCase();
    const visible = results.filter((r) =>
      (filter === 'todos' || (filter === 'fallidos' && r.outcome === 'Failed') || (filter === 'errores' && r.outcome === 'Error') || (filter === 'aprobados' && r.outcome === 'Passed'))
      && (!term || `${r.input} ${r.output || ''}`.toLowerCase().includes(term)));

    render(
      pageHead(run.evaluationName, `Corrida #${run.id} · ${run.agentName} · ${run.datasetName} · ${fmtDate(run.createdAt)}`,
        active ? h('button', { onclick: async () => { await api(`/corridas/${id}/cancelar`, { method: 'POST' }); toast('Cancelando…'); } }, 'Cancelar') : null,
        !active ? h('a', { class: 'button', href: `/api/corridas/${id}/excel` }, 'Descargar Excel') : null,
        h('button', { onclick: () => runEvaluation(run.evaluationId) }, '▶ Ejecutar de nuevo'),
        h('a', { class: 'button', href: `#/evaluaciones/${run.evaluationId}` }, 'Ver evaluación')),
      run.note ? h('div', { class: 'notice info' }, h('strong', null, 'Qué cambió en esta corrida: '), run.note) : null,
      active ? h('div', { class: 'card' },
        h('strong', null, run.status === 'Queued' ? 'En cola…' : `Ejecutando: ${run.completed} de ${run.total} casos`),
        h('div', { class: 'progress' }, h('span', { style: { width: `${run.total ? (run.completed / run.total) * 100 : 0}%` } }))) : null,
      run.status === 'Failed' ? h('div', { class: 'notice bad' }, h('strong', null, 'La corrida falló. '), run.error || '') : null,
      run.status === 'Cancelled' ? h('div', { class: 'notice warn' }, 'La corrida se canceló; se muestran los casos que alcanzaron a ejecutarse.') : null,
      summary.skippedJudgeChecks > 0 ? h('div', { class: 'notice warn' }, `${summary.skippedJudgeChecks} comparaciones con juez IA no se ejecutaron porque el juez no está configurado. Los casos se aprobaron o fallaron solo con el resto de comparaciones.`) : null,
      h('div', { class: 'stats' },
        h('div', { class: 'stat ok' }, h('div', { class: 'value' }, pct(summary.passRate)), h('div', { class: 'label' }, `${summary.passed} de ${results.length} aprobados`)),
        h('div', { class: `stat ${summary.failed ? 'bad' : ''}` }, h('div', { class: 'value' }, summary.failed), h('div', { class: 'label' }, 'fallidos')),
        h('div', { class: `stat ${summary.errors ? 'warn' : ''}` }, h('div', { class: 'value' }, summary.errors), h('div', { class: 'label' }, 'con error (no se pudo evaluar)')),
        h('div', { class: 'stat' }, h('div', { class: 'value' }, `${(summary.averageLatencyMs / 1000).toFixed(1)} s`), h('div', { class: 'label' }, 'tiempo promedio de respuesta')),
        summary.averageTokens != null ? h('div', { class: 'stat' },
          h('div', { class: 'value' }, Math.round(summary.averageTokens).toLocaleString('es')),
          h('div', { class: 'label' }, 'tokens promedio por caso',
            summary.previousAverageTokens != null ? ` (${summary.averageTokens <= summary.previousAverageTokens ? '' : '+'}${Math.round(((summary.averageTokens - summary.previousAverageTokens) / summary.previousAverageTokens) * 100)}% vs anterior)` : null)) : null,
        summary.casesPassingAllAttempts != null ? h('div', { class: 'stat' }, h('div', { class: 'value' }, `${summary.casesPassingAllAttempts}/${summary.cases}`), h('div', { class: 'label' }, 'casos que pasaron en todas las repeticiones')) : null,
        delta != null ? h('div', { class: `stat ${delta > 0 ? 'ok' : delta < 0 ? 'bad' : ''}` },
          h('div', { class: 'value' }, `${delta > 0 ? '+' : ''}${delta} pts`),
          h('div', { class: 'label' }, h('a', { href: `#/resultados/${summary.previousRunId}` }, `vs corrida #${summary.previousRunId}`))) : null),
      h('h2', null, 'Por comparación'),
      h('div', { class: 'table-wrap' }, h('table', null,
        h('thead', null, h('tr', null, h('th', null, 'Comparación'), h('th', null, ''), h('th', null, 'Pasaron'), h('th', null, 'Fallaron'), h('th', null, 'Omitidas'), h('th', null, 'Errores'), h('th', null, 'Puntaje prom.'))),
        h('tbody', null, summary.checks.map((c) => {
          const total = c.passed + c.failed + c.skipped + c.errors || 1;
          return h('tr', null,
            h('td', null, h('strong', null, c.label), c.blocking ? null : h('span', { class: 'badge skip', style: { marginLeft: '6px' } }, 'informativa')),
            h('td', null, h('div', { class: 'bar', title: `${c.passed} pasaron, ${c.failed} fallaron` },
              h('span', { class: 'ok', style: { width: `${(c.passed / total) * 100}%` } }),
              h('span', { class: 'bad', style: { width: `${(c.failed / total) * 100}%` } }),
              h('span', { class: 'warn', style: { width: `${(c.errors / total) * 100}%` } }),
              h('span', { class: 'skip', style: { width: `${(c.skipped / total) * 100}%` } }))),
            h('td', null, c.passed), h('td', null, c.failed), h('td', { class: 'muted' }, c.skipped), h('td', null, c.errors),
            h('td', null, c.averageScore != null ? pct(c.averageScore) : '—'));
        })))),
      summary.changes.length ? h('div', null,
        h('h2', null, `Cambios frente a la corrida #${summary.previousRunId}`),
        h('div', { class: 'card' }, h('ul', { style: { margin: 0 } }, summary.changes.map((ch) =>
          h('li', null, `Caso ${ch.rowIndex + 1}: `, outcomeBadge(ch.before), ' → ', outcomeBadge(ch.after), h('span', { class: 'muted' }, ` ${ch.input.slice(0, 90)}`)))))) : null,
      h('h2', null, 'Casos'),
      h('div', { class: 'actions', style: { marginBottom: '10px' } },
        h('div', { class: 'chips' }, [['todos', 'Todos'], ['fallidos', 'Fallidos'], ['errores', 'Con error'], ['aprobados', 'Aprobados']].map(([key, label]) =>
          h('button', { class: `chip filter${filter === key ? ' active' : ''}`, onclick: () => { filter = key; draw(); } }, `${label} (${counts[key]})`))),
        h('input', { type: 'search', placeholder: 'Buscar en entradas y respuestas…', value: search, style: { maxWidth: '280px' }, oninput: (ev) => { search = ev.target.value; draw(); document.querySelector('input[type=search]')?.focus(); } })),
      h('div', { class: 'table-wrap' }, h('table', null,
        h('thead', null, h('tr', null, h('th', null, '#'), h('th', null, 'Entrada'), h('th', null, 'Respuesta del agente'), h('th', null, 'Resultado'), h('th', null, 'Comparaciones'), h('th', null, 'Tiempo'))),
        h('tbody', null, visible.length ? visible.flatMap((r) => {
          const key = `${r.rowIndex}-${r.attempt}`;
          const rows = [h('tr', { class: 'clickable', onclick: () => { expanded.has(key) ? expanded.delete(key) : expanded.add(key); draw(); } },
            h('td', { class: 'muted' }, r.rowIndex + 1, r.attempt > 1 ? h('div', { class: 'small' }, `intento ${r.attempt}`) : null),
            h('td', { class: 'cell-clip', title: r.input }, r.input),
            h('td', { class: 'cell-clip', title: r.output || r.error || '' }, r.output || h('span', { class: 'muted' }, r.error || '')),
            h('td', null, outcomeBadge(r.outcome)),
            h('td', null, h('div', { class: 'chips nowrap' }, r.checks.map(checkDot))),
            h('td', { class: 'muted small' }, `${(r.latencyMs / 1000).toFixed(1)} s`))];
          if (expanded.has(key)) rows.push(h('tr', { class: 'case-detail' }, h('td', { colspan: 6 }, caseDetail(r))));
          return rows;
        }) : emptyRow(6, active ? 'Esperando resultados…' : 'No hay casos con este filtro.')))));
  };

  await load();
}

function caseDetail(r) {
  return h('div', null,
    h('div', { class: 'grid-2' },
      h('div', null, h('h3', null, 'Entrada'), h('div', { class: 'text-block' }, r.input)),
      h('div', null, h('h3', null, 'Respuesta del agente'), r.output ? h('div', { class: 'text-block' }, r.output) : h('div', { class: 'text-block muted' }, '(vacía)'))),
    r.error ? h('div', { class: 'notice warn', style: { marginTop: '12px' } }, h('strong', null, 'Error: '), r.error) : null,
    r.checks.length ? h('div', { style: { marginTop: '14px' } },
      h('h3', null, 'Comparaciones'),
      h('div', { class: 'check-list' }, r.checks.map((c) => h('div', { class: 'check-item' },
        checkDot(c),
        h('div', null,
          h('strong', null, c.label), c.display ? h('span', { class: 'muted' }, ` · ${c.display}`) : null,
          c.blocking ? null : h('span', { class: 'badge skip', style: { marginLeft: '6px' } }, 'informativa'),
          h('div', null, c.reason),
          c.expected ? h('div', { class: 'small muted' }, 'Esperado: ', c.expected) : null))))) : null,
    r.context.length ? h('details', null, h('summary', null, `Documentos recuperados (${r.context.length})`), r.context.map((c) => h('pre', null, c))) : null,
    r.tools.length ? h('p', null, h('strong', null, 'Herramientas usadas: '), r.tools.join(' → ')) : null,
    r.tokens != null ? h('p', null, h('strong', null, 'Tokens: '), r.tokens.toLocaleString('es')) : null,
    h('details', null, h('summary', null, 'Datos del caso'),
      h('table', { style: { marginTop: '6px' } }, h('tbody', null, Object.entries(r.row).map(([k, v]) => h('tr', null, h('th', { style: { width: '220px' } }, k), h('td', null, v)))))),
    r.rawResponse ? h('details', null, h('summary', null, 'Respuesta completa del agente'), h('pre', null, pretty(r.rawResponse))) : null);
}

// ---------- Navegación ----------

const routes = [
  [/^\/?$/, pageHome, 'inicio'],
  [/^\/agentes$/, pageAgents, 'agentes'],
  [/^\/agentes\/nuevo$/, () => pageAgentForm(null), 'agentes'],
  [/^\/agentes\/(\d+)$/, (m) => pageAgentForm(Number(m[1])), 'agentes'],
  [/^\/casos$/, pageDatasets, 'casos'],
  [/^\/casos\/nuevo$/, pageDatasetNew, 'casos'],
  [/^\/casos\/(\d+)$/, (m) => pageDataset(Number(m[1])), 'casos'],
  [/^\/evaluaciones$/, pageEvaluations, 'evaluaciones'],
  [/^\/evaluaciones\/nueva$/, () => pageEvaluationForm(null), 'evaluaciones'],
  [/^\/evaluaciones\/(\d+)$/, (m) => pageEvaluation(Number(m[1])), 'evaluaciones'],
  [/^\/evaluaciones\/(\d+)\/editar$/, (m) => pageEvaluationForm(Number(m[1])), 'evaluaciones'],
  [/^\/resultados$/, pageRuns, 'resultados'],
  [/^\/resultados\/(\d+)$/, (m) => pageRun(Number(m[1])), 'resultados'],
];

async function route() {
  cleanups.forEach((fn) => fn());
  cleanups = [];
  const path = location.hash.replace(/^#/, '').split('?')[0];
  const match = routes.find(([re]) => re.test(path));
  const [re, page, nav] = match || routes[0];
  document.querySelectorAll('[data-nav]').forEach((a) => a.classList.toggle('active', a.dataset.nav === nav));
  try {
    await page(path.match(re));
  } catch (e) {
    render(h('div', { class: 'notice bad' }, h('strong', null, 'No se pudo cargar la página. '), e.message));
  }
}

async function init() {
  try {
    const [checkTypes, estado] = await Promise.all([api('/comparaciones'), api('/estado')]);
    catalog = { checkTypes, estado };
  } catch (e) {
    toast(`No se pudo conectar con el servidor: ${e.message}`, true);
  }
  document.getElementById('judge-status').replaceChildren(
    h('span', { class: `dot ${catalog.estado.juezConfigurado ? 'ok' : 'skip'}`, style: { width: '10px', height: '10px', marginRight: '6px' } }),
    catalog.estado.juezConfigurado ? `Juez IA: ${catalog.estado.modeloJuez}` : 'Juez IA sin configurar');
  window.addEventListener('hashchange', route);
  route();
}

init();
