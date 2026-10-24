"""Revisión externa con Claude Code de las corridas pendientes de la plataforma de evaluaciones.

  python revision.py preparar <carpeta> [--url http://localhost:5080] [--por-lote 40]
      Pide a la plataforma las corridas con revisión pendiente y escribe lotes (lote-N.md) para calificar.
      Los lotes no incluyen el puntaje del juez de la plataforma: la revisión es a ciegas.

  python revision.py enviar <carpeta> [--url http://localhost:5080] [--revisor Claude]
      Lee las respuestas (lote-N.jsonl), las valida y las envía a la plataforma, una corrida a la vez.

Solo usa la biblioteca estándar de Python.
"""
import argparse
import json
import os
import sys
import urllib.error
import urllib.request

FORMATO = ('Una línea JSON por caso, en el mismo orden: {"resultId": número, "checks": [{"label": "nombre exacto de la comparación", '
           '"score": entero de 1 a 5, "reason": "una frase en español"}]}. Califica solo las comparaciones que aparecen en cada caso.')


def api(url, ruta, cuerpo=None):
    req = urllib.request.Request(url.rstrip("/") + "/api" + ruta, method="POST" if cuerpo is not None else "GET",
                                 data=None if cuerpo is None else json.dumps(cuerpo).encode(), headers={"Content-Type": "application/json"})
    try:
        with urllib.request.urlopen(req, timeout=120) as r:
            return json.load(r)
    except urllib.error.HTTPError as e:
        sys.exit(f"La plataforma respondió {e.code} en {ruta}: {e.read().decode()[:300]}")


def recortar(t, n):
    t = t or ""
    return t if len(t) <= n else t[:n] + " […]"


def preparar(a):
    os.makedirs(a.carpeta, exist_ok=True)
    pendientes = api(a.url, "/revisiones/pendientes")
    if not pendientes:
        print("No hay corridas con revisión pendiente.")
        json.dump([], open(os.path.join(a.carpeta, "manifiesto.json"), "w"))
        return
    manifiesto, n = [], 0
    for p in pendientes:
        paquete = api(a.url, f"/corridas/{p['id']}/revision")
        checks = {c["label"]: c for c in paquete["checks"]}
        casos = paquete["cases"]
        for k in range(0, len(casos), a.por_lote):
            n += 1
            lote = casos[k:k + a.por_lote]
            nombre = f"lote-{n}"
            with open(os.path.join(a.carpeta, nombre + ".md"), "w", encoding="utf-8") as w:
                w.write(f"# Revisión de la corrida #{p['id']} · {p['evaluationName']}{' · variante ' + p['variant'] if p.get('variant') else ''}\n\n")
                w.write("Eres un evaluador estricto. Para cada caso tienes la entrada, la respuesta del agente y, por cada comparación, "
                        "lo esperado. Califica cada comparación de 1 a 5 con su escala y sus instrucciones, leyendo el caso completo. "
                        "Decide por tu cuenta.\n\n# Comparaciones\n")
                for c in checks.values():
                    w.write(f"\n## {c['label']}\nEscala: {c['scale']}\n")
                    if c.get("instructions"):
                        w.write(f"Instrucciones adicionales (tienen prioridad sobre la escala):\n{c['instructions']}\n")
                w.write(f"\n# Formato de salida\n{FORMATO}\n\n# Casos\n")
                for caso in lote:
                    w.write(f"\n## resultId {caso['resultId']}\n\nENTRADA:\n{recortar(caso['input'], 3000)}\n\nRESPUESTA DEL AGENTE:\n{recortar(caso['output'], 3000)}\n")
                    for c in caso["checks"]:
                        w.write(f"\nCOMPARACIÓN \"{c['label']}\" — ESPERADO:\n{recortar(c.get('expected'), 3000) or '(sin valor esperado)'}\n")
                    if caso.get("context") and any(checks[c["label"]].get("usesContext") for c in caso["checks"]):
                        w.write("\nDOCUMENTOS RECUPERADOS POR EL AGENTE:\n" + "\n---\n".join(recortar(x, 1200) for x in caso["context"][:12]) + "\n")
            manifiesto.append({"lote": nombre, "run": p["id"], "casos": [c["resultId"] for c in lote],
                               "checks": {str(c["resultId"]): [x["label"] for x in c["checks"]] for c in lote}})
    json.dump(manifiesto, open(os.path.join(a.carpeta, "manifiesto.json"), "w"), ensure_ascii=False)
    print(f"{len(pendientes)} corrida(s) pendiente(s), {sum(len(m['casos']) for m in manifiesto)} casos en {n} lote(s) en {a.carpeta}")
    for p in pendientes:
        print(f"  #{p['id']} {p['evaluationName']}{' · ' + p['variant'] if p.get('variant') else ''}: {', '.join(p['checks'])}")


def enviar(a):
    manifiesto = json.load(open(os.path.join(a.carpeta, "manifiesto.json")))
    por_corrida, problemas = {}, []
    for m in manifiesto:
        arch = os.path.join(a.carpeta, m["lote"] + ".jsonl")
        if not os.path.exists(arch):
            problemas.append(f"{m['lote']}: falta el archivo de respuestas")
            continue
        vistos = set()
        for i, l in enumerate(open(arch, encoding="utf-8"), 1):
            if not l.strip():
                continue
            try:
                o = json.loads(l)
                rid = int(o["resultId"])
                esperados = set(m["checks"].get(str(rid), []))
                checks = [{"label": c["label"], "score": int(c["score"]), "reason": str(c.get("reason", ""))}
                          for c in o["checks"] if c["label"] in esperados and 1 <= int(c["score"]) <= 5]
            except (json.JSONDecodeError, KeyError, ValueError, TypeError) as e:
                problemas.append(f"{m['lote']} línea {i}: {e}")
                continue
            if rid not in m["casos"]:
                problemas.append(f"{m['lote']} línea {i}: el resultado {rid} no es de este lote")
                continue
            vistos.add(rid)
            por_corrida.setdefault(m["run"], []).append({"resultId": rid, "checks": checks})
        faltan = set(m["casos"]) - vistos
        if faltan:
            problemas.append(f"{m['lote']}: faltan {len(faltan)} casos")
    if problemas:
        print("Problemas (corrígelos o vuelve a calificar esos lotes antes de enviar):")
        print("\n".join("  - " + p for p in problemas))
        if not a.forzar:
            sys.exit(1)
    for run, resultados in por_corrida.items():
        r = api(a.url, f"/corridas/{run}/revision", {"reviewer": a.revisor, "results": resultados})
        print(f"Corrida #{run}: {len(resultados)} casos enviados, estado {r['reviewStatus']}")
        for s in r["summary"]:
            print(f"  {s['label']}: {s['cases']} casos | aprueba juez IA {s['platformPassed']} | aprueba {a.revisor} {s['reviewPassed']} | "
                  f"coinciden {s['agreement']:.0%} | puntaje medio {s['platformAverage']}/5 vs {s['reviewAverage']}/5")


p = argparse.ArgumentParser()
p.add_argument("accion", choices=["preparar", "enviar"])
p.add_argument("carpeta")
p.add_argument("--url", default=os.environ.get("EVAL_URL", "http://localhost:5080"))
p.add_argument("--por-lote", type=int, default=40)
p.add_argument("--revisor", default="Claude")
p.add_argument("--forzar", action="store_true", help="enviar aunque algunos lotes tengan problemas")
a = p.parse_args()
preparar(a) if a.accion == "preparar" else enviar(a)
