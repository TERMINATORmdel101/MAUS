#!/usr/bin/env python3
"""Outil de traduction de MAUS : liste les textes à traduire, ou fusionne des traductions.

  python3 tools/i18n.py missing en            textes source sans traduction anglaise (JSON)
  python3 tools/i18n.py merge es fichier.json  ajoute des traductions (clé française -> texte)
  python3 tools/i18n.py prune en               retire les traductions dont le texte source a disparu
"""
import json, os, re, sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
I18N = os.path.join(ROOT, "src", "Maus.Core", "Localization", "i18n")
CALL = re.compile(r'(?:(?<![\w.])|(?<=Texts\.))T\(\s*("(?:[^"\\]|\\.)*"(?:\s*\+\s*"(?:[^"\\]|\\.)*")*)')
PIECE = re.compile(r'"((?:[^"\\]|\\.)*)"')

def unescape(s):
    return s.encode("latin-1", "backslashreplace").decode("unicode_escape") if "\\" in s else s

def sources():
    found = set()
    for base, dirs, files in os.walk(os.path.join(ROOT, "src")):
        if os.sep + "obj" in base or os.sep + "bin" in base:
            continue
        for f in files:
            if f.endswith(".cs"):
                text = open(os.path.join(base, f), encoding="utf-8").read()
                for m in CALL.finditer(text):
                    literal = "".join(p.group(1) for p in PIECE.finditer(m.group(1)))
                    found.add(literal.replace('\\"', '"').replace("\\\\", "\\").replace("\\n", "\n"))
    return found | catalog_texts()

# Catalogues JSON traduits, et leurs champs de texte (chemins : « [] » = chaque élément d'une liste).
# Même liste dans tests/Maus.Core.Tests/Localization/TranslationCoverageTests.cs.
RULES = ["[].title", "[].explanation", "[].advice", "[].category", "[].expectedLabel", "[].fix.title", "[].fix.gain", "[].fix.risk", "[].fix.warning"]
CATALOGS = {
    "processes.json": ["[].category", "[].what"],
    "m01-audit-rules.json": RULES,
    "m04-privacy-rules.json": RULES,
    "m06-visual-rules.json": RULES,
    "m08-bios-vendors.json": ["vendors[].rescueTool"],
    "m09-gpu-drivers.json": ["branches[].label", "branches[].note"],
    "m12-startup-catalog.json": ["families[].label", "families[].item", "families[].loses", "families[].recommendation"],
}

def walk(node, steps):
    if not steps:
        if isinstance(node, str):
            yield node
        return
    step, rest = steps[0], steps[1:]
    name, is_list = (step[:-2], True) if step.endswith("[]") else (step, False)
    if name:
        node = node.get(name) if isinstance(node, dict) else None
    if node is None:
        return
    for item in (node if is_list else [node]):
        yield from walk(item, rest)

def catalog_texts():
    found = set()
    for name, paths in CATALOGS.items():
        path = os.path.join(ROOT, "src", "Maus.Core", "Catalog", name)
        lines = [l for l in open(path, encoding="utf-8") if not l.lstrip().startswith("//")]
        data = json.loads("".join(lines))
        for spec in paths:
            found.update(walk(data, spec.split(".")))
    return found

def load(lang):
    path = os.path.join(I18N, lang + ".json")
    return json.load(open(path, encoding="utf-8")) if os.path.exists(path) else {}

def save(lang, data):
    with open(os.path.join(I18N, lang + ".json"), "w", encoding="utf-8") as f:
        json.dump(dict(sorted(data.items())), f, ensure_ascii=False, indent=2)
        f.write("\n")

if __name__ == "__main__":
    cmd, lang = sys.argv[1], sys.argv[2]
    data = load(lang)
    if cmd == "missing":
        print(json.dumps(sorted(t for t in sources() if t not in data), ensure_ascii=False, indent=2))
    elif cmd == "merge":
        data.update(json.load(open(sys.argv[3], encoding="utf-8")))
        save(lang, data)
    elif cmd == "prune":
        keep = sources()
        save(lang, {k: v for k, v in data.items() if k in keep})
