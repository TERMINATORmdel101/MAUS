#!/usr/bin/env python3
"""Outil de traduction de MAUS : liste les textes à traduire, ou fusionne des traductions.

  python3 tools/i18n.py missing en            textes source sans traduction anglaise (JSON)
  python3 tools/i18n.py merge es fichier.json  ajoute des traductions (clé française -> texte)
  python3 tools/i18n.py prune en               retire les traductions dont le texte source a disparu
"""
import json, os, re, sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
I18N = os.path.join(ROOT, "src", "Maus.Core", "Localization", "i18n")
CALL = re.compile(r'(?<![\w.])(?:Texts\.)?T\(\s*"((?:[^"\\]|\\.)*)"')

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
                    found.add(m.group(1).replace('\\"', '"').replace("\\\\", "\\").replace("\\n", "\n"))
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
