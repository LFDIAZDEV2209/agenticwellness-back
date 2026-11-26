"""Compara las referencias sintéticas del PR con la rama destino."""

import difflib
import json
import subprocess
import sys


FIXTURES = "tests/CoppAddresd.UnitTests/Wellness365/Fixtures"
NAMES = ("people.json", "golden.json", "reference-plans.json", "reference-histories.json", "live-exercise-golden.json")
MAX_DIFF_LINES = 160


def revision_files(revision):
    listing = subprocess.check_output(
        ["git", "ls-tree", "--name-only", revision, "--", f"{FIXTURES}/"],
        text=True,
        encoding="utf-8",
    )
    return set(listing.splitlines())


def fixture_lines(revision, path):
    raw = subprocess.check_output(
        ["git", "show", f"{revision}:{path}"], text=True, encoding="utf-8"
    )
    return json.dumps(json.loads(raw), indent=2, sort_keys=True, ensure_ascii=False).splitlines()


def report(base):
    baseline, current = revision_files(base), revision_files("HEAD")
    print("## Referencias del Motor 365")
    changed = False
    for name in NAMES:
        path = f"{FIXTURES}/{name}"
        if path not in baseline and path not in current:
            continue
        if path not in baseline or path not in current:
            changed = True
            state = "nueva" if path in current else "eliminada"
            print(f"\n- `{name}`: referencia {state}; consultar la pestaña Files changed.")
            continue
        diff = list(difflib.unified_diff(
            fixture_lines(base, path), fixture_lines("HEAD", path),
            fromfile=f"base/{name}", tofile=f"PR/{name}", lineterm="",
        ))
        if not diff:
            continue
        changed = True
        print(f"\n### `{name}`\n\n```diff")
        print("\n".join(diff[:MAX_DIFF_LINES]))
        print("```")
        if len(diff) > MAX_DIFF_LINES:
            print(f"Se muestran {MAX_DIFF_LINES} de {len(diff)} líneas del diff.")
    if not changed:
        print("\nLas referencias no cambiaron. Cualquier diferencia de salida aparece en el log de paridad.")
    else:
        print("\nSi cambia una regla, incluir su justificación y el enlace al registro de aprobación profesional en el PR.")


if __name__ == "__main__":
    report(sys.argv[1])
