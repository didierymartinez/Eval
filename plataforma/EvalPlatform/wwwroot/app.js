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

// Parámetros de una variante, como "modelo = gpt-5-mini".
function paramChips(params) {
  const entries = Object.entries(params || {});
  if (!entries.length) return null;
  return h('span', { class: 'chips', style: { display: 'inline-flex', marginLeft: '6px', verticalAlign: 'middle' } },
    entries.map(([k, v]) => h('span', { class: 'chip mono' }, `${k} = ${v}`)));
}

const variantLetter = (i) => String.fromCharCode(65 + i);

// Marcadores {{nombre}} de la configuración de un agente (sin {{env:...}}).
function agentPlaceholders(agent) {
  if (!agent) return [];
  const m = agent.mcp || {};
  const kind = agent.kind || 'http';
  const texts = [agent.url, ...Object.values(agent.headers || {}),
    ...(kind === 'http' ? [agent.bodyTemplate] : []),
    ...(kind === 'mcp' ? [m.toolTemplate, m.argumentsTemplate] : []),
    ...(kind === 'mcp_modelo' ? [m.systemPrompt, m.modelName, m.modelEndpoint, m.allowedTools] : [])];
  const names = new Set();
  for (const t of texts) for (const match of String(t || '').matchAll(/\{\{\s*(?:json:)?([^{}:]+?)\s*\}\}/g)) names.add(match[1]);
  return [...names];
}

// Juez IA: el del servidor (id vacío) o el de un equipo.
function judgeSelect(judges, selectedId, onchange) {
  const server = judges.servidor;
  const serverLabel = !server.configurado ? `${server.nombre} — sin configurar`
    : !server.permitido ? `${server.nombre} — no disponible en este servidor` : server.nombre;
  return h('select', { onchange: (ev) => onchange(ev.target.value ? Number(ev.target.value) : null) },
    h('option', { value: '', selected: !selectedId }, serverLabel),
    judges.jueces.map((j) => h('option', { value: j.id, selected: j.id === selectedId }, `${j.name} (${j.model})`)));
}

const judgeUsable = (judges, judgeId) => !!judgeId || (judges.servidor.configurado && judges.servidor.permitido);
const fmtTokens = (n) => (n == null ? '—' : Math.round(n).toLocaleString('es'));

async function compareEvaluation(id, note) {
  try {
    const r = await api(`/evaluaciones/${id}/comparar`, { method: 'POST', body: { note: note || null } });
    go(`#/comparativas/${r.comparisonId}`);
  } catch (e) {
    toast(e.message, true);
  }
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

  const demo = evaluations.filter((e) => e.name.startsWith('Demo')).sort((x, y) => x.id - y.id)[0];
  render(
    pageHead('Evaluaciones de agentes', 'Comprueba con casos reales si tu agente o tu prompt responde lo que esperas.'),
    demo && !demo.lastRun
      ? h('div', { class: 'notice info' },
          h('strong', null, '¿Primera vez? '),
          'Hay una evaluación de ejemplo lista con un agente de helpdesk simulado. ',
          h('button', { class: 'primary', onclick: () => runEvaluation(demo.id, { askNote: false }) }, 'Ejecutar el ejemplo'),
          demo.variantCount >= 2 ? [' ', h('button', { onclick: () => compareEvaluation(demo.id, 'Base actual vs. base con el manual de usuarios') }, '⚖ Comparar dos bases de conocimiento')] : null)
      : null,
    h('div', { class: 'steps' },
      step(1, agents.length > 0, 'Conecta tu agente', 'Dónde está y cómo se le habla: un endpoint o webhook, la API de un modelo, un servidor MCP o tu propio código en la red del agente.', '#/agentes', agents.length ? `${agents.length} agente(s)` : 'Conectar agente'),
      step(2, datasets.length > 0, 'Sube los casos de prueba', 'Un Excel, CSV o Google Sheet con la entrada y lo que esperas en cada caso.', '#/casos', datasets.length ? `${datasets.length} grupo(s) de casos` : 'Subir casos'),
      step(3, evaluations.length > 0, 'Define qué comparar', 'Elige qué revisar en cada respuesta: que contenga algo, que sea correcta según un juez IA, que use los documentos…', '#/evaluaciones', evaluations.length ? `${evaluations.length} evaluación(es)` : 'Crear evaluación'),
      step(4, runs.length > 0, 'Ejecuta y revisa', 'Mira qué casos pasaron, cuáles fallaron y por qué. Compara variantes (modelo, prompt, base de conocimiento) lado a lado y descarga el resultado en Excel.', '#/resultados', runs.length ? 'Ver resultados' : 'Aún no hay resultados')),
    h('h2', null, 'Últimas corridas'),
    runsTable(runs));
}

function runsTable(runs) {
  return h('div', { class: 'table-wrap' }, h('table', null,
    h('thead', null, h('tr', null, h('th', null, 'Evaluación'), h('th', null, 'Agente'), h('th', null, 'Fecha'), h('th', null, 'Estado'), h('th', null, 'Resultado'))),
    h('tbody', null, runs.length ? runs.map((r) =>
      h('tr', { class: 'clickable', onclick: () => go(`#/resultados/${r.id}`) },
        h('td', null, r.evaluationName),
        h('td', { class: 'muted' }, r.agentName, r.variant ? h('div', { class: 'small' }, `Variante: ${r.variant}`) : null),
        h('td', { class: 'muted' }, fmtDate(r.createdAt)),
        h('td', null, runStatusBadge(r.status),
          r.reviewStatus === 'pendiente' ? h('span', { class: 'badge warn', style: { marginLeft: '6px' }, title: 'Claude Code la revisará cuando se lo pidas' }, 'revisión pendiente')
            : r.reviewStatus === 'revisada' ? h('span', { class: 'badge ok', style: { marginLeft: '6px' } }, 'revisada') : null),
        h('td', null, r.status === 'Completed' || r.completed ? `${r.passed} de ${r.completed} aprobados` : '—'))) : emptyRow(5, 'Todavía no se ha ejecutado ninguna evaluación.'))));
}

// ---------- Agentes ----------

async function pageAgents() {
  const agents = await api('/agentes');
  render(
    pageHead('Agentes', 'Dónde está cada agente o prompt que quieres evaluar.', h('a', { class: 'button primary', href: '#/agentes/nuevo' }, '+ Nuevo agente')),
    h('div', { class: 'table-wrap' }, h('table', null,
      h('thead', null, h('tr', null, h('th', null, 'Nombre'), h('th', null, 'Tipo'), h('th', null, 'URL'))),
      h('tbody', null, agents.length ? agents.map((a) =>
        h('tr', { class: 'clickable', onclick: () => go(`#/agentes/${a.id}`) },
          h('td', null, h('strong', null, a.name)),
          h('td', null, h('span', { class: `badge ${a.kind && a.kind !== 'http' ? 'info' : 'skip'}` }, kindLabel(a.kind)),
            a.executor ? h('div', { class: 'small muted' }, `vía ejecutores: ${a.executor}`) : null),
          h('td', { class: 'mono cell-clip' }, a.kind === 'codigo' ? '—' : a.kind && a.kind !== 'http' ? a.url : `${a.method} ${a.url}`))) : emptyRow(3, 'Aún no hay agentes. Crea el primero.')))));
}

const AGENT_KINDS = [
  ['http', 'Agente o API por HTTP', 'Webhook o endpoint propio (C#, Java, Python…) o la API de un modelo.'],
  ['mcp', 'Servidor MCP: llamar herramientas', 'Llama una herramienta del MCP con los argumentos de cada caso y compara el resultado.'],
  ['mcp_modelo', 'Servidor MCP con un modelo', 'Un modelo recibe las herramientas del MCP: evalúa si elige la correcta y con qué argumentos.'],
  ['codigo', 'Tu propio código', 'Cualquier tecnología (librería, gRPC, colas…): tu código pide los casos, llama al agente y devuelve las respuestas.'],
];

async function executorStatus(group) {
  try {
    const s = await api('/ejecutores/estado');
    return { enabled: s.habilitado, group: s.grupos.find((g) => g.grupo === group) };
  } catch {
    return { enabled: false, group: null };
  }
}

function executorBadge(state) {
  const online = (state.group?.ejecutores || []).filter((e) => e.conectado);
  return online.length
    ? h('span', { class: 'badge ok' }, `${online.length} ejecutor(es) conectado(s): ${online.map((e) => e.nombre).join(', ')}`)
    : h('span', { class: 'badge warn' }, 'Ningún ejecutor conectado ahora');
}
// Pasos del agente según sus trazas OpenTelemetry (llamadas al modelo, herramientas).
function traceStepsTable(trace) {
  if (!trace || !trace.spans) return null;
  const fmt = (n) => (n == null ? '—' : n.toLocaleString('es'));
  return h('div', null,
    h('p', { class: 'small', style: { margin: '6px 0' } },
      h('strong', null, 'Por trazas: '),
      `${fmt(trace.inputTokens)} tokens de entrada · ${fmt(trace.outputTokens)} de salida · ${trace.modelCalls} llamada(s) al modelo`,
      trace.models?.length ? ` (${trace.models.join(', ')})` : '', ` · ${trace.spans} span(s) recibidos`),
    trace.steps?.length ? h('div', { class: 'table-wrap' }, h('table', null,
      h('thead', null, h('tr', null, h('th', null, 'Paso'), h('th', null, 'Modelo / herramienta'), h('th', null, 'Entrada'), h('th', null, 'Salida'), h('th', null, 'Duración'))),
      h('tbody', null, trace.steps.map((s) => h('tr', null,
        h('td', { class: 'mono' }, s.name, s.counted ? null : h('span', { class: 'muted small' }, s.inputTokens != null ? ' (incluido en sus pasos internos)' : '')),
        h('td', null, s.tool || s.model || h('span', { class: 'muted' }, '—')),
        h('td', null, fmt(s.inputTokens)), h('td', null, fmt(s.outputTokens)),
        h('td', { class: 'muted small' }, `${Math.round(s.durationMs)} ms`)))))) : null);
}

const kindLabel = (kind) => (AGENT_KINDS.find((k) => k[0] === (kind || 'http')) || AGENT_KINDS[0])[1];
const toolCallText = (t) => (t.arguments ? `${t.name}(${t.arguments})` : t.name);

function mcpToolsTable(tools) {
  return h('div', { class: 'table-wrap', style: { marginTop: '10px' } }, h('table', null,
    h('thead', null, h('tr', null, h('th', null, 'Herramienta'), h('th', null, 'Descripción'), h('th', null, 'Parámetros'))),
    h('tbody', null, tools.length ? tools.map((t) => {
      const props = t.inputSchema?.properties || {};
      const required = new Set(t.inputSchema?.required || []);
      return h('tr', null,
        h('td', { class: 'mono' }, t.name),
        h('td', null, t.description || h('span', { class: 'muted' }, 'sin descripción')),
        h('td', { class: 'small' }, Object.keys(props).length ? Object.entries(props).map(([k, v]) =>
          h('div', null, h('code', null, k), required.has(k) ? ' *' : '', v.type ? h('span', { class: 'muted' }, ` ${v.type}`) : null, v.description ? ` — ${v.description}` : null))
          : h('span', { class: 'muted' }, 'ninguno')));
    }) : emptyRow(3, 'El servidor no expone herramientas.'))));
}

async function pageAgentForm(id) {
  const templates = id ? [] : await api('/plantillas-agente');
  const a = id ? await api(`/agentes/${id}`) : {
    kind: 'http', name: '', url: '', method: 'POST', headers: {}, bodyTemplate: '{ "input": "{{input}}" }',
    responsePath: 'output', contextPath: '', toolsPath: '', toolArgumentsPath: '', tokensPath: '', timeoutSeconds: 120,
    mcp: { toolTemplate: '{{herramienta}}', argumentsTemplate: '{{argumentos}}' },
  };
  a.kind = a.kind || 'http';
  a.remote = !!a.executor;
  const executorInfo = h('div', { style: { marginTop: '8px' } });
  const refreshExecutor = async () => {
    const group = (a.executor || '').trim().toLowerCase();
    if (!group) return replace(executorInfo);
    const state = await executorStatus(group);
    replace(executorInfo,
      !state.enabled ? h('div', { class: 'notice warn small' }, 'La API de ejecutores está apagada en este servidor: configura la variable Ejecutores__Token (ver README).') : null,
      h('div', null, executorBadge(state), state.group?.pendientes ? h('span', { class: 'muted small' }, ` · ${state.group.pendientes} caso(s) esperando`) : null));
  };
  a.mcp = { toolTemplate: '{{herramienta}}', argumentsTemplate: '{{argumentos}}', ...(a.mcp || {}) };
  let headers = Object.entries(a.headers || {});
  let testInput = '';
  const testResult = h('div');
  const toolsResult = h('div');

  const collect = () => ({
    kind: a.kind, name: a.name, url: a.url, method: a.method,
    executor: a.kind === 'codigo' || a.remote ? (a.executor || '').trim().toLowerCase() : null,
    headers: Object.fromEntries(headers.filter(([k]) => k.trim())),
    bodyTemplate: a.bodyTemplate, responsePath: a.responsePath, contextPath: a.contextPath, toolsPath: a.toolsPath,
    toolArgumentsPath: a.toolArgumentsPath, tokensPath: a.tokensPath, timeoutSeconds: Number(a.timeoutSeconds) || 120,
    tracesEnabled: !!a.tracesEnabled, traceWaitSeconds: Number(a.traceWaitSeconds) || 15,
    mcp: { ...a.mcp, temperature: a.mcp.temperature === '' || a.mcp.temperature == null ? null : Number(a.mcp.temperature) },
  });

  const bindIn = (obj, key, attrs = {}) => h(attrs.multiline ? 'textarea' : 'input', {
    type: attrs.multiline ? null : 'text', value: obj[key] ?? '', placeholder: attrs.placeholder,
    rows: attrs.rows, style: attrs.prose ? { fontFamily: 'inherit', fontSize: '15px' } : null,
    oninput: (e) => (obj[key] = e.target.value),
  });
  const bind = (key, attrs) => bindIn(a, key, attrs);
  const bindMcp = (key, attrs) => bindIn(a.mcp, key, attrs);

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

  const listTools = async () => {
    replace(toolsResult, h('p', { class: 'muted' }, 'Conectando con el servidor MCP…'));
    try {
      const tools = await api('/agentes/herramientas', { method: 'POST', body: collect() });
      replace(toolsResult, h('div', { class: 'notice ok' }, h('strong', null, `Conectó: ${tools.length} herramienta(s)`)), mcpToolsTable(tools));
    } catch (e) {
      replace(toolsResult, h('div', { class: 'notice bad' }, e.message));
    }
  };

  const runTest = async () => {
    if (a.kind === 'mcp' && /\{\{\s*(herramienta|argumentos)\s*\}\}/.test(`${a.mcp.toolTemplate}${a.mcp.argumentsTemplate}`)) {
      replace(testResult, h('div', { class: 'notice warn' }, 'La herramienta o los argumentos vienen de columnas de los casos ({{herramienta}}, {{argumentos}}), que aquí no existen. Para probar, pon temporalmente un nombre y argumentos fijos (puedes usar {{input}} con la entrada de ejemplo).'));
      return;
    }
    replace(testResult, h('p', { class: 'muted' }, 'Llamando al agente…'));
    try {
      const r = await api('/agentes/probar', { method: 'POST', body: { agent: collect(), input: testInput } });
      const showContext = a.kind === 'mcp_modelo' || (a.kind === 'http' && a.contextPath);
      const showTools = a.kind !== 'http' || a.toolsPath;
      replace(testResult,
        h('div', { class: `notice ${r.ok ? 'ok' : 'bad'}` },
          h('strong', null, r.ok ? `Conectó en ${r.latencyMs} ms` : 'No funcionó'),
          r.error ? h('div', null, r.error) : null),
        r.ok ? h('div', null,
          h('h3', null, 'Respuesta que se va a evaluar'),
          r.output ? h('div', { class: 'text-block' }, r.output) : h('div', { class: 'notice warn' }, a.kind === 'http'
            ? 'La ruta de la respuesta no encontró nada. Revisa "Dónde viene la respuesta" con el JSON completo de abajo.'
            : 'La respuesta vino vacía.'),
          showTools ? h('p', null, h('strong', null, 'Herramientas: '), r.toolCalls.length
            ? h('span', { class: 'mono' }, r.toolCalls.map(toolCallText).join('  →  '))
            : h('span', { class: 'muted' }, 'ninguna')) : null,
          showContext ? h('p', null, h('strong', null, a.kind === 'mcp_modelo' ? `Resultados de herramientas: ${r.context.length}` : `Documentos recuperados: ${r.context.length}`),
            r.context.length || a.kind !== 'http' ? null : h('span', { class: 'muted' }, ' (la ruta no encontró documentos)')) : null,
          r.context.length ? h('details', null, h('summary', null, 'Ver'), r.context.map((c) => h('pre', null, c))) : null,
          r.tokens != null || (a.kind === 'http' && a.tokensPath) ? h('p', null, h('strong', null, 'Tokens: '), r.tokens ?? h('span', { class: 'muted' }, 'la ruta no encontró tokens')) : null,
          a.tracesEnabled ? (r.trace
            ? traceStepsTable(r.trace)
            : h('div', { class: 'notice warn small' }, `No llegaron trazas del agente en ${a.traceWaitSeconds || 15} s. Revisa que exporte a ${location.origin} (sección "Tokens por trazas" de abajo) y que propague el encabezado traceparent.`)) : null) : null,
        h('details', null, h('summary', null, 'Lo que se envió'), h('pre', null, pretty(r.requestBody || '') || '(sin cuerpo)')),
        r.raw ? h('details', { open: !r.ok || !r.output }, h('summary', null, 'Respuesta completa'), h('pre', null, pretty(r.raw))) : null);
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

  const envHelp = h('span', null, 'Para claves o tokens usa ', h('code', null, '{{env:NOMBRE}}'), ': se lee de una variable de entorno del servidor y no queda guardado aquí.');

  const httpCards = () => [
    h('div', { class: 'card' },
      field('Nombre', bind('name', { placeholder: 'Ej. Agente de helpdesk (producción)' })),
      h('div', { class: 'grid-3' },
        field('URL', bind('url', { placeholder: 'https://…' }), 'Puede llevar {{columna}} si cambia por caso.'),
        field('Método', h('select', { onchange: (e) => (a.method = e.target.value) },
          ['POST', 'GET', 'PUT', 'PATCH'].map((m) => h('option', { value: m, selected: a.method === m }, m)))),
        field('Tiempo máximo (seg.)', h('input', { type: 'number', min: 1, max: 1800, value: a.timeoutSeconds, oninput: (e) => (a.timeoutSeconds = e.target.value) }))),
      field('Encabezados', headersEditor(), envHelp),
      field('Cuerpo (JSON)', bind('bodyTemplate', { multiline: true, rows: 6 }),
        h('span', null, h('code', null, '{{input}}'), ' se reemplaza por la entrada de cada caso y ', h('code', null, '{{nombre_columna}}'), ' por cualquier otra columna de tus casos.'))),
    h('div', { class: 'card' },
      h('h3', null, 'Qué sacar de la respuesta'),
      h('p', { class: 'muted small' }, 'Rutas dentro del JSON que devuelve el agente. Ejemplos: ', h('code', null, 'output'), ', ', h('code', null, 'output.respuesta'), ', ', h('code', null, 'choices[0].message.content'), ', ', h('code', null, 'intermediateSteps[*].observation'), '. Usa "Probar" para ver el JSON completo.'),
      field('Dónde viene la respuesta', bind('responsePath', { placeholder: 'output (vacío = toda la respuesta)' })),
      h('div', { class: 'grid-2' },
        field('Dónde vienen los documentos recuperados (opcional, RAG)', bind('contextPath', { placeholder: 'intermediateSteps[*].observation' }), 'Necesario para las comparaciones de base de conocimiento.'),
        field('Dónde vienen los tokens consumidos (opcional)', bind('tokensPath', { placeholder: 'usage.total_tokens' }),
          'Varias rutas separadas por coma se suman (ej. usage.input_tokens, usage.output_tokens).')),
      h('div', { class: 'grid-2' },
        field('Dónde vienen las herramientas usadas (opcional)', bind('toolsPath', { placeholder: 'intermediateSteps[*].action.tool' }), 'Necesario para "Usó las herramientas".'),
        field('Dónde vienen sus argumentos (opcional)', bind('toolArgumentsPath', { placeholder: 'intermediateSteps[*].action.toolInput' }), 'En el mismo orden que las herramientas. Necesario para "Llamó con estos argumentos".'))),
  ];

  const mcpCommon = () => h('div', { class: 'card' },
    field('Nombre', bind('name', { placeholder: 'Ej. MCP de cartera (pruebas)' })),
    h('div', { class: 'grid-2' },
      field('URL del servidor MCP', bind('url', { placeholder: 'https://tu-servidor/mcp' }), 'Transporte HTTP (Streamable HTTP o SSE). Los servidores stdio no están soportados.'),
      field('Tiempo máximo (seg.)', h('input', { type: 'number', min: 1, max: 1800, value: a.timeoutSeconds, oninput: (e) => (a.timeoutSeconds = e.target.value) }))),
    field('Encabezados (autenticación)', headersEditor(), envHelp),
    h('button', { type: 'button', onclick: listTools }, 'Ver herramientas del servidor'),
    toolsResult);

  const mcpDirectCard = () => h('div', { class: 'card' },
    h('h3', null, 'Qué herramienta llamar en cada caso'),
    h('div', { class: 'grid-2' },
      field('Herramienta', bindMcp('toolTemplate', { placeholder: '{{herramienta}}' }),
        h('span', null, 'Un nombre fijo (', h('code', null, 'consultar_cartera'), ') o una columna de tus casos (', h('code', null, '{{herramienta}}'), ').')),
      field('Argumentos (JSON)', bindMcp('argumentsTemplate', { multiline: true, rows: 3, placeholder: '{{argumentos}}' }),
        h('span', null, h('code', null, '{{argumentos}}'), ' toma el JSON de esa columna. También puedes armarlo: ', h('code', null, '{ "cliente": "{{cliente}}" }'), '.'))),
    h('p', { class: 'muted small' }, 'Lo que devuelve la herramienta es la respuesta a evaluar. Para revisar si debía fallar, compara ', h('code', null, 'isError'), ' con "Es igual a" (opción "Comparar otro campo de la respuesta"). ',
      h('code', null, 'structuredContent.campo'), ' compara un campo del resultado estructurado.'));

  const mcpModelCard = () => h('div', { class: 'card' },
    h('h3', null, 'Modelo que usa las herramientas'),
    h('p', { class: 'muted small' }, 'El modelo recibe la entrada de cada caso y las herramientas del MCP (nombres, descripciones y parámetros). Así se ve si tus descripciones le alcanzan para elegir bien. Cualquier API compatible con OpenAI.'),
    h('div', { class: 'grid-3' },
      field('Modelo', bindMcp('modelName', { placeholder: 'Vacío = el mismo del juez IA' })),
      field('URL de la API', bindMcp('modelEndpoint', { placeholder: 'https://api.openai.com/v1/' })),
      field('Temperatura', h('input', { type: 'number', step: '0.1', min: 0, max: 2, value: a.mcp.temperature ?? '', placeholder: 'por defecto', oninput: (e) => (a.mcp.temperature = e.target.value) }))),
    field('Clave de la API', bindMcp('modelApiKey', { placeholder: '{{env:OPENAI_API_KEY}}' }), envHelp),
    field('Prompt de sistema', bindMcp('systemPrompt', { multiline: true, rows: 3, prose: true, placeholder: 'Ej. Eres el asistente de cartera. Usa las herramientas para consultar datos; no inventes cifras.' })),
    field('Herramientas que se ofrecen (opcional)', bindMcp('allowedTools', { placeholder: 'consultar_cartera, buscar_cliente' }), 'Separadas por coma. Vacío = todas las del servidor.'));

  const groupInput = () => h('input', {
    type: 'text', value: a.executor || '', placeholder: 'red-finanzas',
    oninput: (e) => { a.executor = e.target.value; }, onchange: refreshExecutor,
  });

  const traceCard = () => h('div', { class: 'card' },
    h('h3', null, 'Tokens por trazas (OpenTelemetry)'),
    h('label', { class: 'inline' },
      h('input', { type: 'checkbox', checked: !!a.tracesEnabled, onchange: (e) => { a.tracesEnabled = e.target.checked; draw(); } }),
      'Medir tokens y pasos con las trazas del agente'),
    h('p', { class: 'help' }, 'Para servicios que solo devuelven el resultado. La plataforma manda el encabezado traceparent en cada caso; el agente, si está instrumentado con OpenTelemetry, continúa esa traza y envía sus spans a la plataforma. Se suman los tokens de cada llamada al modelo (convenciones GenAI) sin cambiar la respuesta del servicio.'),
    a.tracesEnabled ? h('div', null,
      field('Esperar las trazas hasta (segundos)', h('input', { type: 'number', min: 1, max: 45, value: a.traceWaitSeconds || 15, style: { maxWidth: '160px' }, oninput: (e) => (a.traceWaitSeconds = e.target.value) }),
        'Los exportadores envían por lotes, unos segundos después de responder.'),
      h('details', { open: true }, h('summary', null, 'Cómo configurar el agente'),
        h('p', { class: 'small' }, 'Variables de entorno del servicio del agente (cualquier lenguaje con OpenTelemetry):'),
        h('pre', null,
`OTEL_EXPORTER_OTLP_ENDPOINT=${location.origin}
OTEL_EXPORTER_OTLP_PROTOCOL=http/protobuf
OTEL_BSP_SCHEDULE_DELAY=500        # enviar rápido en ambientes de evaluación
OTEL_SERVICE_NAME=mi-agente`),
        h('p', { class: 'small' }, 'C# con Microsoft.Extensions.AI (paquetes OpenTelemetry.Extensions.Hosting, OpenTelemetry.Exporter.OpenTelemetryProtocol y OpenTelemetry.Instrumentation.AspNetCore):'),
        h('pre', null,
`builder.Services.AddOpenTelemetry().WithTracing(t => t
    .AddSource("mi-agente")
    .AddAspNetCoreInstrumentation()   // continúa el traceparent que manda la plataforma
    .AddOtlpExporter());

IChatClient chat = cliente.AsIChatClient()
    .AsBuilder()
    .UseOpenTelemetry(sourceName: "mi-agente")   // registra cada llamada con sus tokens
    .Build();`),
        h('p', { class: 'small muted' }, 'Semantic Kernel, LangChain/OpenLLMetry, OpenAI Agents SDK y otros frameworks emiten los mismos atributos (gen_ai.usage.input_tokens / output_tokens). Detrás de un ejecutor remoto, el agente exporta igual a la plataforma.'))) : null);

  const whereCard = () => h('div', { class: 'card' },
    h('h3', null, '¿Desde dónde se llama al agente?'),
    h('label', { class: 'inline', style: { marginBottom: '6px' } },
      h('input', { type: 'radio', name: 'where', checked: !a.remote, onchange: () => { a.remote = false; draw(); } }),
      'Desde la plataforma (el agente es alcanzable desde aquí)'),
    h('label', { class: 'inline' },
      h('input', { type: 'radio', name: 'where', checked: a.remote, onchange: () => { a.remote = true; draw(); } }),
      'Desde un ejecutor dentro de la red del agente'),
    a.remote ? h('div', { style: { marginTop: '12px' } },
      field('Grupo de ejecutores', groupInput(),
        h('span', null, 'El ejecutor se instala en la red del agente y solo hace conexiones salientes hacia la plataforma: ',
          h('code', null, 'EvalPlatform ejecutor --plataforma <url> --grupo <grupo>'), '. Las variables ', h('code', null, '{{env:…}}'), ' se leen en el ejecutor, así que los secretos se quedan en esa red.')),
      executorInfo) : null);

  const codeCard = () => h('div', { class: 'card' },
    field('Nombre', bind('name', { placeholder: 'Ej. Agente de cobranza (servicio Java)' })),
    field('Grupo de ejecutores', groupInput(), 'Nombre con que tu código pide los casos. Letras minúsculas, números, - o _.'),
    executorInfo,
    h('h3', { style: { marginTop: '14px' } }, 'Cómo funciona'),
    h('ol', { class: 'small', style: { margin: '0 0 10px', paddingLeft: '20px' } },
      h('li', null, 'Ejecutas la evaluación aquí; los casos quedan esperando.'),
      h('li', null, 'Tu código, en la red y la tecnología del agente, pide casos: ', h('code', null, 'POST /api/ejecutores/tomar'), '.'),
      h('li', null, 'Llama al agente como sea (librería, gRPC, cola, consola) y devuelve la respuesta: ', h('code', null, 'POST /api/ejecutores/resultado'), '.'),
      h('li', null, 'La plataforma califica con las comparaciones y el juez IA, y muestra los resultados.')),
    h('p', { class: 'small muted' }, 'Hay un ejecutor de ejemplo en Python sin dependencias en ', h('code', null, 'plataforma/ejecutores/python/ejecutor.py'), ': solo cambias la función que llama a tu agente. Detalle de la API en el README.'),
    h('details', null, h('summary', null, 'Ver la API con curl'), h('pre', null,
`# 1. Pedir casos (espera hasta 20 s si no hay)
curl -X POST $PLATAFORMA/api/ejecutores/tomar \\
  -H "X-Ejecutor-Token: $TOKEN" -H "Content-Type: application/json" \\
  -d '{"group": "${a.executor || 'mi-grupo'}", "worker": "mi-servicio", "waitSeconds": 20}'
# → [{ "taskId": 15, "input": "¿Cuánto debe el cliente 123?", "row": { ... } }]

# 2. Devolver la respuesta del agente
curl -X POST $PLATAFORMA/api/ejecutores/resultado \\
  -H "X-Ejecutor-Token: $TOKEN" -H "Content-Type: application/json" \\
  -d '{"taskId": 15, "output": "Debe $1.200.000",
       "toolCalls": [{"name": "consultar_cartera", "arguments": {"cliente": "123"}}],
       "context": [], "tokens": 180, "latencyMs": 950}'`)));

  const testCard = () => h('div', { class: 'card' },
    h('h3', null, 'Probar'),
    field(a.kind === 'mcp' ? 'Entrada de ejemplo (se usa si la herramienta o los argumentos tienen {{input}})' : 'Escribe una entrada de ejemplo',
      h('textarea', { rows: 2, style: { fontFamily: 'inherit', fontSize: '15px', minHeight: '0' }, placeholder: a.kind === 'http' ? 'Ej. No puedo entrar, me dice que mi usuario está bloqueado' : 'Ej. ¿Cuánto debe el cliente 123?', oninput: (e) => (testInput = e.target.value) })),
    a.remote ? h('div', { class: 'notice info small' }, 'Probar llama al agente desde la plataforma. Si el agente solo es alcanzable desde la red del ejecutor, la prueba va a fallar aunque esté bien configurado: en ese caso ejecuta una evaluación con pocos casos.') : null,
    h('button', { type: 'button', onclick: runTest }, 'Probar'),
    testResult);

  const form = () => h('div', null,
    h('div', { class: 'card' },
      h('label', null, 'Tipo'),
      h('div', { class: 'templates' }, AGENT_KINDS.map(([kind, label, desc]) => h('button', {
        type: 'button', class: `template${a.kind === kind ? ' selected' : ''}`, 'aria-pressed': a.kind === kind ? 'true' : 'false',
        onclick: () => { a.kind = kind; draw(); },
      }, h('strong', null, label), h('span', null, desc))))),
    a.kind === 'codigo' ? codeCard()
      : [a.kind === 'http' ? httpCards() : [mcpCommon(), a.kind === 'mcp' ? mcpDirectCard() : mcpModelCard()], a.kind === 'mcp_modelo' ? null : traceCard(), whereCard(), testCard()],
    h('div', { class: 'actions' },
      h('button', { class: 'primary', onclick: save }, 'Guardar agente'),
      h('a', { class: 'button', href: '#/agentes' }, 'Volver'),
      id ? h('button', { class: 'danger', onclick: () => confirmAndDelete('¿Eliminar este agente?', `/agentes/${id}`, '#/agentes') }, 'Eliminar') : null));

  const container = h('div');
  const draw = () => { replace(container, form()); refreshExecutor(); };
  draw();

  render(
    pageHead(id ? a.name : 'Nuevo agente', id ? 'Edita cómo se conecta la plataforma con este agente.' : 'Empieza desde una plantilla o llena los datos a mano.'),
    templates.length ? h('div', null,
      h('h3', null, 'Plantillas'),
      h('div', { class: 'templates' }, templates.map((t) => h('button', {
        class: 'template', type: 'button',
        onclick: () => {
          Object.assign(a, {
            ...t.agent,
            kind: t.agent.kind || 'http', method: t.agent.method || 'POST', bodyTemplate: t.agent.bodyTemplate || '',
            contextPath: t.agent.contextPath || '', toolsPath: t.agent.toolsPath || '', toolArgumentsPath: t.agent.toolArgumentsPath || '', tokensPath: t.agent.tokensPath || '',
            mcp: { toolTemplate: '{{herramienta}}', argumentsTemplate: '{{argumentos}}', ...(t.agent.mcp || {}) },
          });
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
      let judgeId = null;
      const judgeBox = h('div');
      api('/jueces').then((judges) => {
        if (!judgeUsable(judges, null) && judges.jueces.length) judgeId = judges.jueces[0].id;
        replace(judgeBox, field('Juez IA que genera los casos', judgeSelect(judges, judgeId, (v) => { judgeId = v; }), 'Los tokens se cuentan en el juez elegido.'));
        button.disabled = !judgeUsable(judges, judgeId) && !judges.jueces.length;
      }).catch(() => {});
      const mcpPicker = h('select', null, h('option', { value: '' }, '— Traer las herramientas de un servidor MCP —'));
      api('/agentes').then((agents) => agents.filter((x) => x.kind && x.kind !== 'http').forEach((x) => mcpPicker.append(h('option', { value: x.id }, x.name))));
      mcpPicker.onchange = async () => {
        if (!mcpPicker.value) return;
        try {
          const agent = await api(`/agentes/${mcpPicker.value}`);
          const list = await api('/agentes/herramientas', { method: 'POST', body: agent });
          tools.value = list.map((t) => {
            const params = Object.entries(t.inputSchema?.properties || {}).map(([k, v]) => `${k}${v.type ? `: ${v.type}` : ''}`).join(', ');
            return `${t.name}(${params}) — ${t.description || ''}`;
          }).join('\n');
          toast(`Se trajeron ${list.length} herramientas`);
        } catch (e) { toast(e.message, true); }
      };
      const button = h('button', {
        class: 'primary',
        onclick: async () => {
          button.disabled = true;
          button.textContent = 'Generando… (puede tardar un minuto)';
          try {
            const r = await api('/datasets/generar', { method: 'POST', body: { name: name.value, description: description.value, tools: tools.value, knowledge: knowledge.value, count: Number(count.value), judgeId } });
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
        h('div', { class: 'notice info small' }, 'El modelo arma un primer borrador con preguntas frecuentes, casos límite y casos que el agente debe escalar o rechazar. Revísalo: descárgalo en Excel, corrige lo que no esté bien y vuelve a subirlo (o pásalo a un Google Sheet). Necesita un juez IA: el del servidor o el de tu equipo (menú Jueces IA).'),
        field('Nombre', name),
        field('Qué hace el agente', description),
        h('div', { class: 'grid-2' },
          field('Herramientas que puede usar', h('div', null, tools, h('div', { style: { marginTop: '6px' } }, mcpPicker))),
          field('Cantidad de casos', count, 'Entre 3 y 50.')),
        field('Información que tiene disponible', knowledge),
        judgeBox,
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
  const { evaluation: e, runs, comparisons } = await api(`/evaluaciones/${id}`);
  const [agents, datasets, judges] = await Promise.all([api('/agentes'), api('/datasets'), api('/jueces')]);
  const agent = agents.find((a) => a.id === e.agentId);
  const agentName = (agentId) => agents.find((a) => a.id === agentId)?.name || '(agente borrado)';
  const dataset = datasets.find((d) => d.id === e.datasetId);
  const completed = runs.filter((r) => r.status === 'Completed' && !r.variant);
  const noteInput = h('input', { type: 'text', placeholder: 'Ej.: prompt v2 con árbol de decisión, temperatura 0.2, agregué el manual de nómina' });
  const variantSelect = e.variants.length
    ? h('select', { 'aria-label': 'Variante', style: { maxWidth: '260px' } },
        h('option', { value: '' }, 'Agente tal cual (sin variante)'),
        e.variants.map((v) => h('option', { value: v.name }, `Variante: ${v.name}`)))
    : null;
  const selected = new Set();
  const compareSelected = h('button', { disabled: true, onclick: () => go(`#/comparar?corridas=${[...selected].join(',')}`) }, '⚖ Comparar seleccionadas');
  const refreshSelected = () => {
    compareSelected.disabled = selected.size < 2;
    compareSelected.textContent = selected.size ? `⚖ Comparar seleccionadas (${selected.size})` : '⚖ Comparar seleccionadas';
  };

  const runOne = async () => {
    try {
      const r = await api(`/evaluaciones/${id}/ejecutar`, { method: 'POST', body: { note: noteInput.value || null, variant: variantSelect?.value || null } });
      go(`#/resultados/${r.runId}`);
    } catch (err) {
      toast(err.message, true);
    }
  };

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
      e.repetitions > 1 ? h('p', { class: 'muted small' }, `Cada caso se ejecuta ${e.repetitions} veces.`) : null,
      e.checks.some((c) => checkInfo(c.type)?.usesJudge)
        ? h('p', { class: 'muted small' }, 'Juez IA: ', e.judgeId ? h('a', { href: `#/jueces/${e.judgeId}` }, judges.jueces.find((j) => j.id === e.judgeId)?.name || '(borrado)') : judges.servidor.nombre)
        : null,
      e.variants.length ? [
        h('h3', { style: { marginTop: '14px' } }, 'Variantes'),
        h('ul', null, e.variants.map((v, i) => h('li', null,
          h('strong', null, `${variantLetter(i)}. ${v.name}`),
          v.agentId ? h('span', { class: 'muted' }, ` · agente: ${agentName(v.agentId)}`) : null,
          paramChips(v.parameters)))),
      ] : null),
    h('div', { class: 'card' },
      h('label', null, e.variants.length >= 2 ? '¿Qué estás probando? (opcional)' : '¿Qué cambiaste desde la última corrida? (opcional)'),
      h('div', { class: 'kv-row', style: { marginBottom: 0, flexWrap: 'wrap' } },
        noteInput,
        e.variants.length >= 2 ? h('button', { class: 'primary', onclick: () => compareEvaluation(id, noteInput.value) }, `⚖ Comparar las ${e.variants.length} variantes`) : null,
        variantSelect,
        h('button', { class: e.variants.length >= 2 ? '' : 'primary', onclick: runOne }, '▶ Ejecutar ahora')),
      h('div', { class: 'help' }, e.variants.length >= 2
        ? 'Comparar corre los mismos casos con cada variante y los muestra lado a lado. «Ejecutar ahora» corre una sola configuración.'
        : ['Queda en el historial para comparar el antes y el después de cada cambio. Para probar varias configuraciones a la vez (modelo, prompt, base de conocimiento), agrega ',
           h('a', { href: `#/evaluaciones/${id}/editar` }, 'variantes'), '.'])),
    comparisons.length ? [
      h('h2', null, 'Comparativas'),
      h('div', { class: 'table-wrap' }, h('table', null,
        h('thead', null, h('tr', null, h('th', null, 'Comparativa'), h('th', null, 'Fecha'), h('th', null, 'Qué se compara'), h('th', null, 'Variantes'))),
        h('tbody', null, comparisons.map((c) =>
          h('tr', { class: 'clickable', onclick: () => go(`#/comparativas/${c.id}`) },
            h('td', null, `#${c.id}`),
            h('td', { class: 'muted' }, fmtDate(c.createdAt)),
            h('td', { class: 'cell-clip', title: c.note || '' }, c.note || h('span', { class: 'muted' }, '—')),
            h('td', null, h('div', { class: 'chips' }, c.runs.map((r) => h('span', { class: 'chip' }, `${r.variant}: `, passRateBadge(r)))))))))),
    ] : null,
    h('h2', null, 'Historial'),
    completed.length > 1 ? trendBars(completed.slice(0, 12).reverse()) : null,
    runs.length > 1 ? h('div', { class: 'actions', style: { marginBottom: '10px' } }, compareSelected,
      h('span', { class: 'small muted' }, 'Marca dos o más corridas para verlas lado a lado (por ejemplo, antes y después de un cambio).')) : null,
    h('div', { class: 'table-wrap' }, h('table', null,
      h('thead', null, h('tr', null, h('th', null, ''), h('th', null, 'Corrida'), h('th', null, 'Fecha'), h('th', null, 'Qué cambió'), h('th', null, 'Estado'), h('th', null, 'Aprobados'), h('th', null, 'Fallidos'), h('th', null, 'Errores'))),
      h('tbody', null, runs.length ? runs.map((r) =>
        h('tr', { class: 'clickable', onclick: () => go(`#/resultados/${r.id}`) },
          h('td', { onclick: (ev) => ev.stopPropagation() },
            h('input', { type: 'checkbox', 'aria-label': `Comparar corrida ${r.id}`, onchange: (ev) => { ev.target.checked ? selected.add(r.id) : selected.delete(r.id); refreshSelected(); } })),
          h('td', null, `#${r.id}`),
          h('td', { class: 'muted' }, fmtDate(r.createdAt)),
          h('td', { class: 'cell-clip', title: r.note || '' },
            r.variant ? h('span', { class: 'badge info', style: { marginRight: '6px' } }, r.variant) : null,
            r.note || (r.variant ? null : h('span', { class: 'muted' }, '—'))),
          h('td', null, runStatusBadge(r.status)),
          h('td', null, `${r.passed} (${pct(r.passRate)})`),
          h('td', null, r.failed),
          h('td', null, r.errors))) : emptyRow(8, 'Todavía no se ha ejecutado.')))));
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
  add('herramientas', find(/^(herramientas_esperadas|herramientas|expected_tools|tools)$/i));
  add('argumentos', find(/argumentos_esperados|expected_arguments|expected_args/i));
  const errorColumn = find(/^(es_error|debe_fallar|expected_error)$/i);
  if (errorColumn) out.push({ type: 'igual', label: '¿Debía dar error?', expectedColumn: errorColumn, responsePath: 'isError', blocking: true });
  add('igual', find(/^(categoria_esperada|clase|etiqueta|label|decision|debe_responder)$/i));
  return out;
}

async function pageEvaluationForm(id) {
  const [agents, datasets, judges] = await Promise.all([api('/agentes'), api('/datasets'), api('/jueces')]);
  const e = id ? (await api(`/evaluaciones/${id}`)).evaluation : {
    name: '', agentId: agents[0]?.id, datasetId: Number(query().get('dataset')) || datasets[0]?.id,
    inputColumn: '', checks: [], repetitions: 1, variants: [],
  };
  // Una evaluación importada no tiene agente: se propone el primero.
  if (!agents.some((a) => a.id === Number(e.agentId))) e.agentId = agents[0]?.id;
  // Si el juez del servidor no se puede usar, se propone el primero de los equipos.
  if (!id && !judgeUsable(judges, null) && judges.jueces.length) e.judgeId = judges.jueces[0].id;
  // Los parámetros se editan como pares para poder tener filas vacías mientras se escriben.
  e.variants = (e.variants || []).map((v) => ({ ...v, pairs: Object.entries(v.parameters || {}) }));
  if (!agents.length || !datasets.length) {
    render(pageHead('Nueva evaluación'),
      h('div', { class: 'notice warn' }, 'Para crear una evaluación primero necesitas ',
        !agents.length ? h('a', { href: '#/agentes/nuevo' }, 'un agente') : null,
        !agents.length && !datasets.length ? ' y ' : null,
        !datasets.length ? h('a', { href: '#/casos/nuevo' }, 'casos de prueba') : null, '.'));
    return;
  }

  const columnsOf = () => datasets.find((d) => d.id === Number(e.datasetId))?.columns || [];
  const guessInput = (cols) => cols.find((c) => /^(input|entrada|pregunta|ticket|mensaje|prompt|caso)$/i.test(c)) || cols[0] || '';
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
      h('details', { open: !!(c.label || c.responsePath || c.onlyWhenPath || c.judgeInstructions) },
        h('summary', null, 'Opciones avanzadas'),
        h('div', { class: 'grid-2', style: { marginTop: '8px' } },
          field('Nombre para mostrar', h('input', { type: 'text', value: c.label || '', placeholder: info?.label, oninput: (ev) => (c.label = ev.target.value) })),
          field('Comparar otro campo de la respuesta', h('input', { type: 'text', value: c.responsePath || '', placeholder: 'Ej. output.puede_responder', oninput: (ev) => (c.responsePath = ev.target.value) }),
            'Ruta dentro del JSON del agente. Vacío = la respuesta principal.'),
          field('Evaluar solo cuando el campo', h('input', { type: 'text', value: c.onlyWhenPath || '', placeholder: 'Ej. puede_responder', oninput: (ev) => (c.onlyWhenPath = ev.target.value || null) }),
            'Vacío = siempre. Si el campo no tiene el valor de al lado, la comparación se omite (y no gasta juez).'),
          field('tenga el valor', h('input', { type: 'text', value: c.onlyWhenValue || '', placeholder: 'Ej. sí', oninput: (ev) => (c.onlyWhenValue = ev.target.value || null) }))),
        info?.usesJudge && ['correccion', 'rubrica', 'cobertura'].includes(c.type)
          ? field('Instrucciones para el juez', h('textarea', { rows: 4, value: c.judgeInstructions || '', placeholder: 'Ej. No exijas datos puntuales que solo se obtienen revisando la base del cliente (valores, fechas, adjuntos). Si falta la ruta del menú o un paso necesario, el puntaje máximo es 3.', oninput: (ev) => (c.judgeInstructions = ev.target.value || null) }),
            'Se agregan a las instrucciones del juez en esta evaluación.')
          : null));
  };

  const agentOf = (agentId) => agents.find((a) => a.id === Number(agentId));
  // Parámetros que se le pueden cambiar a un agente: sus {{marcadores}} que no son columnas del caso.
  const paramsOf = (agent) => agentPlaceholders(agent).filter((n) => n.toLowerCase() !== 'input' && !columnsOf().includes(n));

  const variantCard = (v, i) => {
    const agent = agentOf(v.agentId || e.agentId);
    const available = paramsOf(agent);
    return h('div', { class: 'check-card' },
      h('div', { class: 'check-head' },
        h('span', { class: 'badge info' }, variantLetter(i)),
        h('input', { type: 'text', 'aria-label': 'Nombre de la variante', value: v.name, placeholder: 'Ej. gpt-5-mini + prompt v2', style: { flex: 1, fontWeight: 600 }, oninput: (ev) => (v.name = ev.target.value) }),
        h('button', { type: 'button', class: 'link danger', onclick: () => { e.variants.splice(i, 1); draw(); } }, 'Quitar')),
      h('div', { class: 'grid-2' },
        field('Agente', h('select', { onchange: (ev) => { v.agentId = ev.target.value ? Number(ev.target.value) : null; draw(); } },
          h('option', { value: '', selected: !v.agentId }, `El de la evaluación (${agentOf(e.agentId)?.name || '—'})`),
          agents.filter((a) => a.id !== Number(e.agentId)).map((a) => h('option', { value: a.id, selected: a.id === v.agentId }, a.name))),
          'Otro agente sirve para comparar, por ejemplo, dos proveedores de modelos.'),
        h('div', { class: 'field' },
          h('label', null, 'Parámetros'),
          v.pairs.map((pair, j) => h('div', { class: 'kv-row' },
            h('input', { type: 'text', value: pair[0], placeholder: 'Nombre (ej. modelo)', list: `params-${i}`, oninput: (ev) => (pair[0] = ev.target.value.trim()) }),
            h('input', { type: 'text', value: pair[1], placeholder: 'Valor (ej. gpt-5-mini)', oninput: (ev) => (pair[1] = ev.target.value) }),
            h('button', { type: 'button', class: 'link', title: 'Quitar', onclick: () => { v.pairs.splice(j, 1); draw(); } }, 'Quitar'))),
          h('datalist', { id: `params-${i}` }, available.map((n) => h('option', { value: n }))),
          h('button', { type: 'button', onclick: () => { v.pairs.push([available.find((n) => !v.pairs.some((p) => p[0] === n)) || '', '']); draw(); } }, '+ Parámetro'),
          h('div', { class: 'help' }, agent?.kind === 'codigo'
            ? ['Tu ejecutor recibe estos parámetros en ', h('code', null, 'parameters'), ' con cada caso.']
            : available.length
              ? ['Este agente usa: ', available.map((n) => h('code', { style: { marginRight: '4px' } }, `{{${n}}}`)), '.']
              : ['Este agente no tiene parámetros. Escribe ', h('code', null, '{{modelo}}'), ', ', h('code', null, '{{prompt}}'), ' o el nombre que quieras en su configuración (cuerpo, URL o prompt) para poder cambiarlo por variante. ',
                 agent ? h('a', { href: `#/agentes/${agent.id}` }, 'Editar el agente') : null]))));
  };

  const addVariant = () => {
    const available = paramsOf(agentOf(e.agentId));
    e.variants.push({ name: e.variants.length ? `Variante ${variantLetter(e.variants.length)}` : 'Actual', agentId: null, pairs: available.map((n) => [n, '']) });
    draw();
  };

  const save = async (andRun) => {
    const body = {
      ...e,
      agentId: Number(e.agentId),
      datasetId: Number(e.datasetId),
      repetitions: Number(e.repetitions) || 1,
      judgeId: e.judgeId || null,
      variants: e.variants.map((v) => ({
        name: v.name,
        agentId: v.agentId ? Number(v.agentId) : null,
        parameters: Object.fromEntries(v.pairs.filter((p) => p[0])),
      })),
    };
    try {
      const saved = id ? await api(`/evaluaciones/${id}`, { method: 'PUT', body }) : await api('/evaluaciones', { method: 'POST', body });
      toast('Evaluación guardada');
      if (andRun && saved.variants.length >= 2) compareEvaluation(saved.id);
      else if (andRun) runEvaluation(saved.id, { askNote: !!id });
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
          field('Agente', h('select', { onchange: (ev) => { e.agentId = Number(ev.target.value); draw(); } },
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
            'Los modelos no responden siempre igual. Con 3 ves si un caso pasa siempre o solo a veces.')),
        h('div', { class: 'grid-2' },
          field('Juez IA', judgeSelect(judges, e.judgeId, (v) => { e.judgeId = v; draw(); }),
            ['Califica las comparaciones (IA) y (RAG). Los tokens se cuentan en el juez elegido: cada equipo puede usar ', h('a', { href: '#/jueces' }, 'el suyo'), '.']),
          h('div', null,
            h('label', { class: 'inline' }, h('input', { type: 'checkbox', checked: !!e.requiresReview, onchange: (ev) => (e.requiresReview = ev.target.checked) }), 'Revisar con Claude (revisión externa)'),
            h('div', { class: 'help' }, 'Cada corrida queda con revisión pendiente. Claude Code vuelve a calificar las comparaciones con juez cuando se lo pidas o en una tarea programada, y aquí se ve cuánto coincide con el juez IA.')))),
      h('h2', null, 'Variantes a comparar (opcional)'),
      h('p', { class: 'muted', style: { marginTop: 0 } }, 'Corre los mismos casos con distintas configuraciones y míralas lado a lado: otro modelo, otro prompt, más o menos documentos, con o sin base de conocimiento. Cada parámetro llega al agente como ', h('code', null, '{{nombre}}'), '. Para números o booleanos en el cuerpo JSON usa ', h('code', null, '"{{json:nombre}}"'), '.'),
      e.variants.map(variantCard),
      e.variants.length === 1 ? h('div', { class: 'notice info' }, 'Agrega al menos otra variante para poder comparar.') : null,
      h('div', { class: 'actions', style: { marginBottom: '8px' } },
        h('button', { type: 'button', onclick: addVariant }, '+ Agregar variante')),
      h('h2', null, 'Qué comparar en cada respuesta'),
      usesJudge && !e.judgeId && !judges.servidor.configurado
        ? h('div', { class: 'notice warn' }, 'Hay comparaciones con juez IA, pero el juez del servidor no está configurado. Esas comparaciones se van a omitir: elige el juez de tu equipo arriba o configura el del servidor (ver README, sección "Juez IA").')
        : usesJudge && !e.judgeId && !judges.servidor.permitido
          ? h('div', { class: 'notice warn' }, 'Este servidor no permite usar su juez en las evaluaciones. Elige el juez de tu equipo arriba (o créalo en ', h('a', { href: '#/jueces' }, 'Jueces IA'), ').')
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
  const [runs, evaluations] = await Promise.all([api('/corridas?limite=100'), api('/evaluaciones')]);
  const file = h('input', { type: 'file', accept: '.json,application/json' });
  const target = h('select', null,
    h('option', { value: '' }, 'Crear una evaluación nueva con sus casos'),
    evaluations.map((e) => h('option', { value: e.id }, `Agregar a: ${e.name}`)));
  const importBox = h('details', { class: 'card', style: { marginTop: 0 } },
    h('summary', null, 'Importar resultados de otra plataforma'),
    h('p', { class: 'muted small' }, 'El archivo sale del botón «Exportar» de una corrida o una comparativa. Sirve para traer resultados de una red aislada (sin salida) a esta plataforma.'),
    h('div', { class: 'grid-2' }, field('Archivo (.json)', file), field('Dónde', target)),
    h('button', {
      class: 'primary',
      onclick: async () => {
        if (!file.files[0]) return toast('Elige el archivo exportado.', true);
        const form = new FormData();
        form.append('archivo', file.files[0]);
        if (target.value) form.append('evaluacion', target.value);
        try {
          const r = await api('/importar', { method: 'POST', form });
          toast(`Se importaron ${r.runIds.length} corrida(s).`);
          go(r.comparisonId ? `#/comparativas/${r.comparisonId}` : `#/resultados/${r.runIds[0]}`);
        } catch (e) {
          toast(e.message, true);
        }
      },
    }, 'Importar'));
  render(pageHead('Resultados', 'Todas las corridas, de la más reciente a la más antigua.'), importBox, runsTable(runs));
}

async function pageRun(id) {
  let filter = 'todos';
  let search = '';
  const expanded = new Set();
  let results = [];
  let data;
  let timer;
  let executor = null;

  let review = null;
  const load = async () => {
    [data, results] = await Promise.all([api(`/corridas/${id}`), api(`/corridas/${id}/resultados`)]);
    review = data.run.reviewStatus === 'revisada' ? await api(`/corridas/${id}/revision/resumen`) : null;
    if (data.run.executor && (data.run.status === 'Running' || data.run.status === 'Queued')) executor = await executorStatus(data.run.executor);
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
        !active ? h('a', { class: 'button', href: `/api/corridas/${id}/exportar`, title: 'Archivo para importar en otra plataforma (por ejemplo, desde una red aislada)' }, 'Exportar') : null,
        h('button', { onclick: () => runEvaluation(run.evaluationId) }, '▶ Ejecutar de nuevo'),
        h('a', { class: 'button', href: `#/evaluaciones/${run.evaluationId}` }, 'Ver evaluación')),
      run.importedFrom ? h('div', { class: 'notice info' }, h('strong', null, 'Importada de: '), run.importedFrom) : null,
      run.variant ? h('div', { class: 'notice info' },
        h('strong', null, 'Variante: '), run.variant, paramChips(run.parameters),
        run.comparisonId ? [' · ', h('a', { href: `#/comparativas/${run.comparisonId}` }, 'Ver la comparativa con las demás variantes')] : null) : null,
      run.note ? h('div', { class: 'notice info' }, h('strong', null, 'Qué cambió en esta corrida: '), run.note) : null,
      active ? h('div', { class: 'card' },
        h('strong', null, run.status === 'Queued' ? 'En cola…' : `Ejecutando: ${run.completed} de ${run.total} casos`),
        h('div', { class: 'progress' }, h('span', { style: { width: `${run.total ? (run.completed / run.total) * 100 : 0}%` } })),
        run.executor && executor ? h('div', { class: 'small' },
          `Los casos los ejecuta el grupo de ejecutores "${run.executor}". `, executorBadge(executor),
          !(executor.group?.ejecutores || []).some((e) => e.conectado)
            ? h('div', { class: 'muted', style: { marginTop: '6px' } }, 'La corrida avanza cuando un ejecutor de ese grupo se conecte. Los casos esperan; no se pierden si la plataforma se reinicia.')
            : null) : null) : null,
      run.status === 'Failed' ? h('div', { class: 'notice bad' }, h('strong', null, 'La corrida falló. '), run.error || '') : null,
      run.status === 'Cancelled' ? h('div', { class: 'notice warn' }, 'La corrida se canceló; se muestran los casos que alcanzaron a ejecutarse.') : null,
      reviewBox(run, review, data.checks, active, async (accion) => { await api(`/corridas/${id}/revision/${accion}`, { method: 'POST' }); await load(); }),
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
        summary.judgeTokens != null ? h('div', { class: 'stat' },
          h('div', { class: 'value' }, fmtTokens(summary.judgeTokens)),
          h('div', { class: 'label' }, `tokens del juez${run.judgeName ? ` (${run.judgeName})` : ''}`)) : null,
        summary.averageModelCalls != null ? h('div', { class: 'stat' },
          h('div', { class: 'value' }, summary.averageModelCalls.toLocaleString('es', { maximumFractionDigits: 1 })),
          h('div', { class: 'label' }, 'llamadas al modelo por caso (trazas)')) : null,
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
          if (expanded.has(key)) rows.push(h('tr', { class: 'case-detail' }, h('td', { colspan: 6 }, caseDetail(r, run.reviewer))));
          return rows;
        }) : emptyRow(6, active ? 'Esperando resultados…' : 'No hay casos con este filtro.')))));
  };

  await load();
}

// Revisión externa (Claude Code): estado, botón para marcarla y, si ya se hizo, cuánto coincide con el juez IA.
function reviewBox(run, review, checks, active, act) {
  const usesJudge = (checks || []).some((c) => checkInfo(c.type)?.usesJudge);
  if (!usesJudge || active) return null;
  if (run.reviewStatus === 'pendiente') {
    return h('div', { class: 'notice info' }, h('strong', null, 'Revisión de Claude pendiente. '),
      'Se califica cuando pidas en Claude Code "revisa las corridas pendientes" o en la tarea programada. ',
      h('button', { class: 'small', onclick: () => act('quitar') }, 'Quitar de pendientes'));
  }
  if (run.reviewStatus !== 'revisada') {
    return h('div', { class: 'actions', style: { margin: '6px 0' } },
      h('button', { class: 'small', onclick: () => act('marcar'), title: 'Claude Code volverá a calificar las comparaciones con juez de esta corrida' }, 'Marcar para revisión de Claude'));
  }
  return h('div', null,
    h('h2', null, `Revisión de ${run.reviewer || 'Claude'}`, h('span', { class: 'muted small', style: { marginLeft: '8px', fontWeight: 'normal' } }, fmtDate(run.reviewedAt))),
    h('div', { class: 'table-wrap' }, h('table', null,
      h('thead', null, h('tr', null, h('th', null, 'Comparación'), h('th', null, 'Casos'), h('th', null, 'Aprueba el juez IA'), h('th', null, `Aprueba ${run.reviewer || 'el revisor'}`),
        h('th', null, 'Coinciden'), h('th', null, 'Puntaje medio juez IA'), h('th', null, `Puntaje medio ${run.reviewer || 'revisor'}`))),
      h('tbody', null, (review?.summary || []).map((c) => h('tr', null,
        h('td', null, h('strong', null, c.label)), h('td', null, c.cases), h('td', null, c.platformPassed), h('td', null, c.reviewPassed),
        h('td', null, h('span', { class: `badge ${c.agreement >= 0.85 ? 'ok' : c.agreement >= 0.7 ? 'warn' : 'bad'}` }, pct(c.agreement))),
        h('td', null, `${c.platformAverage.toFixed(2)}/5`), h('td', null, `${c.reviewAverage.toFixed(2)}/5`)))))),
    h('button', { class: 'small', style: { marginTop: '6px' }, onclick: () => act('marcar'), title: 'Vuelve a dejarla pendiente para otra revisión' }, 'Revisar otra vez'));
}

function caseDetail(r, reviewer) {
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
          ...(r.reviewChecks || []).filter((rv) => rv.label === c.label).map((rv) => h('div', { class: 'small', style: { marginTop: '4px' } },
            checkDot(rv), h('strong', null, ` ${reviewer || 'Revisor'}: `), rv.display, rv.status !== c.status ? h('span', { class: 'badge warn', style: { marginLeft: '6px' } }, 'no coincide con el juez') : null,
            h('div', { class: 'muted' }, rv.reason))),
          c.expected ? h('div', { class: 'small muted' }, 'Esperado: ', c.expected) : null))))) : null,
    r.context.length ? h('details', null, h('summary', null, `Documentos recuperados (${r.context.length})`), r.context.map((c) => h('pre', null, c))) : null,
    (r.toolCalls || []).length
      ? h('div', { style: { marginTop: '10px' } }, h('strong', null, 'Herramientas usadas'),
          h('ol', { style: { margin: '4px 0 0', paddingLeft: '22px' } }, r.toolCalls.map((t) => h('li', null,
            h('span', { class: 'mono' }, toolCallText(t)),
            t.result ? h('details', { style: { marginTop: '2px' } }, h('summary', { class: 'small' }, 'resultado'), h('pre', null, t.result)) : null))))
      : r.tools.length ? h('p', null, h('strong', null, 'Herramientas usadas: '), r.tools.join(' → ')) : null,
    r.tokens != null ? h('p', null, h('strong', null, 'Tokens: '), r.tokens.toLocaleString('es')) : null,
    r.trace && r.trace.spans ? h('details', { open: true }, h('summary', null, 'Pasos del agente (trazas)'), traceStepsTable(r.trace)) : null,
    h('details', null, h('summary', null, 'Datos del caso'),
      h('table', { style: { marginTop: '6px' } }, h('tbody', null, Object.entries(r.row).map(([k, v]) => h('tr', null, h('th', { style: { width: '220px' } }, k), h('td', null, v)))))),
    r.rawResponse ? h('details', null, h('summary', null, 'Respuesta completa del agente'), h('pre', null, pretty(r.rawResponse))) : null);
}

// ---------- Comparativas ----------

// Varias corridas de la misma evaluación lado a lado: una comparativa guardada (id) o corridas elegidas (runIds).
async function pageComparison(id, runIds) {
  let data;
  let timer;
  let filter = 'todos';
  const expanded = new Set();

  const load = async () => {
    data = await api(id ? `/comparativas/${id}` : `/comparativas/corridas?ids=${runIds.join(',')}`);
    draw();
    if (!data.report.finished) timer = setTimeout(load, 2000);
  };
  cleanups.push(() => clearTimeout(timer));

  const draw = () => {
    const { comparison, runs, report } = data;
    if (!runs.length) {
      render(pageHead(`Comparativa #${id}`),
        h('div', { class: 'notice warn' }, 'Esta comparativa ya no tiene corridas (se borraron). ',
          h('button', { class: 'danger', onclick: () => confirmAndDelete('¿Eliminar esta comparativa?', `/comparativas/${id}`, '#/evaluaciones') }, 'Eliminarla')));
      return;
    }
    const variants = report.variants;
    const first = runs[0];
    const best = report.bestRunId;
    const nameOf = (v, i) => `${variantLetter(i)}. ${v.name}`;
    const anyTokens = variants.some((v) => v.averageTokens != null);
    const anyModelCalls = variants.some((v) => v.averageModelCalls != null);
    const anyRepeats = variants.some((v) => v.casesPassingAllAttempts != null);
    const differing = report.cases.filter((c) => c.differs).length;
    const visible = report.cases.filter((c) => filter === 'todos' || c.differs);
    const cellText = (c) => (c.outcome ? [outcomeBadge(c.outcome), c.attempts > 1 ? h('span', { class: 'small muted' }, ` ${c.passed}/${c.attempts}`) : null] : h('span', { class: 'muted' }, '—'));

    render(
      pageHead(`Comparativa · ${first.evaluationName}`,
        `${comparison ? `#${comparison.id} · ` : ''}${first.datasetName} · ${fmtDate(comparison?.createdAt || first.createdAt)} · ${variants.length} variantes`,
        comparison && !report.finished ? h('button', { onclick: async () => { await api(`/comparativas/${id}/cancelar`, { method: 'POST' }); toast('Cancelando…'); } }, 'Cancelar') : null,
        comparison && report.finished ? h('a', { class: 'button', href: `/api/comparativas/${id}/excel` }, 'Descargar Excel') : null,
        comparison && report.finished ? h('a', { class: 'button', href: `/api/comparativas/${id}/exportar`, title: 'Archivo para importar en otra plataforma' }, 'Exportar') : null,
        h('a', { class: 'button', href: `#/evaluaciones/${first.evaluationId}` }, 'Ver evaluación'),
        comparison && report.finished ? h('button', { class: 'danger', onclick: () => confirmAndDelete('¿Eliminar esta comparativa y sus corridas?', `/comparativas/${id}`, `#/evaluaciones/${first.evaluationId}`) }, 'Eliminar') : null),
      comparison?.note ? h('div', { class: 'notice info' }, h('strong', null, 'Qué se compara: '), comparison.note) : null,
      report.verdict ? h('div', { class: 'notice ok' }, h('strong', null, 'Conclusión: '), report.verdict) : null,
      !report.finished ? h('div', { class: 'card' },
        h('strong', null, 'Ejecutando las variantes una tras otra…'),
        variants.map((v, i) => h('div', { style: { marginTop: '8px' } },
          h('div', { class: 'small' }, nameOf(v, i), ' · ', runStatusBadge(v.status), ` ${v.completed} de ${v.total}`),
          h('div', { class: 'progress' }, h('span', { style: { width: `${v.total ? (v.completed / v.total) * 100 : 0}%` } }))))) : null,
      h('div', { class: 'table-wrap' }, h('table', null,
        h('thead', null, h('tr', null,
          h('th', null, 'Variante'), h('th', null, 'Aprobados'), h('th', null, ''), h('th', null, 'Fallidos'), h('th', null, 'Errores'),
          h('th', null, 'Tiempo prom.'), anyTokens ? h('th', null, 'Tokens prom.') : null, anyModelCalls ? h('th', null, 'Llamadas al modelo') : null,
          anyRepeats ? h('th', null, 'Pasan siempre') : null)),
        h('tbody', null, variants.map((v, i) => {
          const done = v.completed || 1;
          return h('tr', { class: 'clickable', onclick: () => go(`#/resultados/${v.runId}`) },
            h('td', null, h('strong', null, nameOf(v, i)), v.runId === best ? h('span', { class: 'badge ok', style: { marginLeft: '6px' } }, '★ mejor') : null,
              h('div', { class: 'small muted' }, v.agentName, paramChips(v.parameters))),
            h('td', null, h('strong', null, pct(v.passRate)), h('div', { class: 'small muted' }, `${v.passed} de ${v.completed}`)),
            h('td', null, h('div', { class: 'bar' },
              h('span', { class: 'ok', style: { width: `${(v.passed / done) * 100}%` } }),
              h('span', { class: 'bad', style: { width: `${(v.failed / done) * 100}%` } }),
              h('span', { class: 'warn', style: { width: `${(v.errors / done) * 100}%` } }))),
            h('td', null, v.failed), h('td', null, v.errors),
            h('td', null, `${(v.averageLatencyMs / 1000).toFixed(1)} s`),
            anyTokens ? h('td', null, v.averageTokens != null ? Math.round(v.averageTokens).toLocaleString('es') : '—') : null,
            anyModelCalls ? h('td', null, v.averageModelCalls != null ? v.averageModelCalls.toLocaleString('es', { maximumFractionDigits: 1 }) : '—') : null,
            anyRepeats ? h('td', null, v.casesPassingAllAttempts != null ? `${v.casesPassingAllAttempts} casos` : '—') : null);
        })))),
      report.checkLabels.length ? [
        h('h2', null, 'Por comparación'),
        h('div', { class: 'table-wrap' }, h('table', null,
          h('thead', null, h('tr', null, h('th', null, 'Comparación (% de casos que pasaron)'), variants.map((v, i) => h('th', null, nameOf(v, i))))),
          h('tbody', null, report.checkLabels.map((label, k) => {
            const rates = variants.map((v) => v.checkPassRates[k]);
            const top = Math.max(...rates.filter((r) => r != null));
            return h('tr', null, h('td', null, h('strong', null, label)),
              variants.map((v) => {
                const rate = v.checkPassRates[k];
                const score = v.checkAverageScores[k];
                return h('td', null,
                  rate == null ? h('span', { class: 'muted', title: 'No se calificó: omitida (por ejemplo, sin juez IA) o sin casos que aplicaran' }, '—') : h('span', { style: { fontWeight: rate === top && rates.some((r) => r != null && r < top) ? 700 : 400 } }, pct(rate)),
                  score != null && Math.abs(score - rate) > 0.005 ? h('div', { class: 'small muted' }, `puntaje ${pct(score)}`) : null);
              }));
          })))),
      ] : null,
      h('h2', null, 'Casos'),
      h('div', { class: 'chips', style: { marginBottom: '10px' } },
        [['todos', `Todos (${report.cases.length})`], ['difieren', `Solo donde cambia el resultado (${differing})`]].map(([key, label]) =>
          h('button', { class: `chip filter${filter === key ? ' active' : ''}`, onclick: () => { filter = key; draw(); } }, label))),
      h('div', { class: 'table-wrap' }, h('table', null,
        h('thead', null, h('tr', null, h('th', null, '#'), h('th', null, 'Entrada'), variants.map((v, i) => h('th', null, nameOf(v, i))))),
        h('tbody', null, visible.length ? visible.flatMap((c) => {
          const rows = [h('tr', { class: 'clickable', onclick: () => { expanded.has(c.rowIndex) ? expanded.delete(c.rowIndex) : expanded.add(c.rowIndex); draw(); } },
            h('td', { class: 'muted' }, c.rowIndex + 1, c.differs ? h('div', { class: 'small', title: 'El resultado cambia entre variantes' }, '≠') : null),
            h('td', { class: 'cell-clip', title: c.input }, c.input),
            c.cells.map((cell) => h('td', null, cellText(cell),
              cell.tokens != null ? h('div', { class: 'small muted' }, `${Math.round(cell.tokens).toLocaleString('es')} tokens`) : null)))];
          if (expanded.has(c.rowIndex)) {
            rows.push(h('tr', { class: 'case-detail' }, h('td', { colspan: 2 + variants.length },
              h('h3', null, 'Entrada'), h('div', { class: 'text-block' }, c.input),
              h('div', { style: { display: 'grid', gridTemplateColumns: `repeat(${variants.length}, minmax(220px, 1fr))`, gap: '12px', marginTop: '14px', overflowX: 'auto' } },
                c.cells.map((cell, i) => h('div', null,
                  h('h3', null, nameOf(variants[i], i), ' ', cellText(cell)),
                  cell.output ? h('div', { class: 'text-block' }, cell.output) : h('div', { class: 'text-block muted' }, cell.error || '(sin respuesta)'),
                  h('div', { class: 'small muted', style: { marginTop: '4px' } },
                    cell.latencyMs != null ? `${(cell.latencyMs / 1000).toFixed(1)} s` : null,
                    cell.tokens != null ? ` · ${Math.round(cell.tokens).toLocaleString('es')} tokens` : null,
                    ' · ', h('a', { href: `#/resultados/${cell.runId}` }, 'ver comparaciones del caso'))))))));
          }
          return rows;
        }) : emptyRow(2 + variants.length, report.finished ? 'No hay casos con este filtro.' : 'Esperando resultados…')))));
  };

  await load();
}

// ---------- Jueces IA ----------

// Uso del mes contra el límite.
function usageBar(used, limit) {
  if (!limit) return h('span', null, `${fmtTokens(used)} tokens`, h('span', { class: 'small muted' }, ' · sin límite'));
  const ratio = Math.min(1, used / limit);
  return h('div', null,
    h('div', { class: 'small' }, `${fmtTokens(used)} de ${fmtTokens(limit)} tokens`),
    h('div', { class: 'bar', style: { marginTop: '4px' } },
      h('span', { class: ratio >= 1 ? 'bad' : ratio >= 0.8 ? 'warn' : 'ok', style: { width: `${ratio * 100}%` } })));
}

async function pageJudges() {
  const data = await api('/jueces');
  const s = data.servidor;
  render(
    pageHead('Jueces IA', `El modelo que califica las comparaciones (IA) y (RAG) y genera casos. Uso de ${data.mes}.`,
      h('a', { class: 'button primary', href: '#/jueces/nuevo' }, '+ Juez de mi equipo')),
    h('div', { class: 'notice info' },
      'Cada equipo puede registrar su propio juez (su endpoint, su modelo y su clave) y elegirlo en sus evaluaciones: así los tokens se cobran a su cuenta y no a la del servidor. ',
      'Sirve cualquier API compatible con OpenAI, incluso un modelo local (Ollama, vLLM) sin costo por token.'),
    h('div', { class: 'table-wrap' }, h('table', null,
      h('thead', null, h('tr', null, h('th', null, 'Juez'), h('th', null, 'Modelo'), h('th', null, 'Uso este mes'), h('th', null, 'Llamadas'), h('th', null, 'Evaluaciones'))),
      h('tbody', null,
        h('tr', null,
          h('td', null, h('strong', null, 'Juez del servidor'),
            h('div', { class: 'small muted' }, !s.configurado ? 'Sin configurar (Juez__Endpoint, Juez__Model, Juez__ApiKey)' : !s.permitido ? 'No disponible para evaluaciones en este servidor' : 'Lo configura quien administra el servidor')),
          h('td', { class: 'mono' }, s.modelo || '—'),
          h('td', null, usageBar(s.tokensMes, s.limiteTokensMes)),
          h('td', null, s.llamadasMes),
          h('td', null, s.evaluaciones)),
        data.jueces.map((j) => h('tr', { class: 'clickable', onclick: () => go(`#/jueces/${j.id}`) },
          h('td', null, h('strong', null, j.name), h('div', { class: 'small muted' }, j.endpoint || 'api.openai.com')),
          h('td', { class: 'mono' }, j.model),
          h('td', null, usageBar(j.tokensMes, j.monthlyTokenLimit)),
          h('td', null, j.llamadasMes),
          h('td', null, j.evaluaciones)))))),
    h('p', { class: 'muted small' }, 'Si el modelo no informa cuántos tokens usó, se estiman (unos 4 caracteres por token). Al llegar al límite, las comparaciones con juez quedan como «Error» hasta el mes siguiente o hasta que se suba el límite.'));
}

async function pageJudgeForm(id) {
  const data = await api('/jueces');
  const existing = id ? data.jueces.find((j) => j.id === id) : null;
  if (id && !existing) throw new Error('El juez no existe.');
  const j = existing ? { ...existing, apiKey: '' } : { name: '', endpoint: '', model: '', apiKey: '', monthlyTokenLimit: null };
  const result = h('div');
  const body = () => ({ name: j.name, endpoint: j.endpoint || null, model: j.model, apiKey: j.apiKey || null, monthlyTokenLimit: j.monthlyTokenLimit || null });

  const test = async () => {
    replace(result, h('div', { class: 'notice info' }, 'Probando…'));
    try {
      const r = await api('/jueces/probar', { method: 'POST', body: { id: id || null, endpoint: j.endpoint || null, model: j.model || null, apiKey: j.apiKey || null } });
      replace(result, r.ok
        ? h('div', { class: 'notice ok' }, h('strong', null, 'Funciona. '), `Respondió «${(r.respuesta || '').slice(0, 80)}» en ${(r.latencyMs / 1000).toFixed(1)} s con ${fmtTokens(r.tokens)} tokens.`)
        : h('div', { class: 'notice bad' }, h('strong', null, 'No funcionó: '), r.error));
    } catch (e) {
      replace(result, h('div', { class: 'notice bad' }, e.message));
    }
  };
  const save = async () => {
    try {
      if (id) await api(`/jueces/${id}`, { method: 'PUT', body: body() });
      else await api('/jueces', { method: 'POST', body: body() });
      toast('Juez guardado');
      go('#/jueces');
    } catch (e) {
      toast(e.message, true);
    }
  };
  const input = (key, attrs) => h('input', { type: 'text', value: j[key] ?? '', ...attrs, oninput: (ev) => (j[key] = ev.target.value) });

  render(
    pageHead(id ? `Juez: ${existing.name}` : 'Nuevo juez IA', 'Un modelo con API compatible con OpenAI, con la clave de tu equipo.'),
    h('div', { class: 'card' },
      field('Nombre', input('name', { placeholder: 'Ej. Equipo de soporte · gpt-5-mini' })),
      h('div', { class: 'grid-2' },
        field('Endpoint', input('endpoint', { placeholder: 'Vacío = https://api.openai.com/v1/' }),
          ['Ej.: ', h('code', null, 'https://<recurso>.openai.azure.com/openai/v1/'), ', ', h('code', null, 'http://ollama:11434/v1/'), '.']),
        field('Modelo', input('model', { placeholder: 'Ej. gpt-5-mini, llama3.1' }))),
      h('div', { class: 'grid-2' },
        field('Clave de la API', input('apiKey', { type: 'password', autocomplete: 'off', placeholder: existing?.claveGuardada ? `Guardada (${existing.claveGuardada}); escribe para reemplazarla` : 'Vacía si el modelo no pide clave' }),
          ['Se guarda cifrada y no se vuelve a mostrar. También puede ser ', h('code', null, '{{env:VARIABLE}}'), ' para leerla del servidor.']),
        field('Límite de tokens por mes', h('input', { type: 'number', min: 1, step: 1000, value: j.monthlyTokenLimit ?? '', placeholder: 'Vacío = sin límite', oninput: (ev) => (j.monthlyTokenLimit = ev.target.value ? Number(ev.target.value) : null) }),
          existing ? ['Este mes: ', fmtTokens(existing.tokensMes), ' tokens en ', existing.llamadasMes, ' llamadas.'] : 'Al llegar al límite, las comparaciones con juez quedan como «Error» hasta el mes siguiente.')),
      result,
      h('div', { class: 'actions' },
        h('button', { class: 'primary', onclick: save }, 'Guardar'),
        h('button', { onclick: test }, 'Probar'),
        h('a', { class: 'button', href: '#/jueces' }, 'Cancelar'),
        existing ? h('button', { class: 'danger', onclick: () => confirmAndDelete('¿Eliminar este juez?', `/jueces/${id}`, '#/jueces') }, 'Eliminar') : null)));
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
  [/^\/comparativas\/(\d+)$/, (m) => pageComparison(Number(m[1])), 'evaluaciones'],
  [/^\/jueces$/, pageJudges, 'jueces'],
  [/^\/jueces\/nuevo$/, () => pageJudgeForm(null), 'jueces'],
  [/^\/jueces\/(\d+)$/, (m) => pageJudgeForm(Number(m[1])), 'jueces'],
  [/^\/comparar$/, () => pageComparison(null, (query().get('corridas') || '').split(',').map(Number).filter(Boolean)), 'evaluaciones'],
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
    catalog.estado.juezConfigurado ? `Juez del servidor: ${catalog.estado.modeloJuez}` : 'Juez del servidor sin configurar');
  window.addEventListener('hashchange', route);
  route();
}

init();
