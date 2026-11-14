#!/usr/bin/env python3
"""Archiva únicamente default-83w; simula por defecto, con respaldo y reversa comprobada.

Usa PGHOST/PGPORT/PGDATABASE/PGUSER/PGPASSWORD del entorno. Nunca imprime DSN,
credenciales ni pacientes. No toca inscripciones, semanas, tareas ni medios.
"""
import argparse
from copy import deepcopy
import json
import os
from pathlib import Path
import sys

import psycopg2

CODE = "default-83w"
OPERATION = "archive-default-83w-v1"


class ArchiveConflict(Exception):
    """El estado actual ya no coincide con el respaldo de esta operación."""


def read_backup(path):
    with Path(path).open(encoding="utf-8") as source:
        backup = json.load(source)
    if backup.get("operation") != OPERATION or any(
        not isinstance(backup.get(part), dict) or backup[part].get("code") != CODE
        for part in ("before", "after")
    ) or backup["before"].get("id") != backup["after"].get("id"):
        raise ArchiveConflict("El respaldo no pertenece a esta operación.")
    return backup


def save_backup(path, backup):
    destination = Path(path)
    if destination.exists():
        if read_backup(destination) != backup:
            raise ArchiveConflict("Ya existe otro respaldo; no se sobrescribe.")
        return
    # Exclusivo y durable antes del UPDATE: un fallo de BD deja una reversa inocua.
    descriptor = os.open(destination, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
    with os.fdopen(descriptor, "w", encoding="utf-8", newline="\n") as target:
        json.dump(backup, target, ensure_ascii=False, sort_keys=True)
        target.write("\n")
        target.flush()
        os.fsync(target.fileno())


def run(args):
    reverse = args.reverse or args.mode == "reverse"
    writing = args.mode != "simulate"
    if (writing or reverse) and not args.backup:
        raise ArchiveConflict("La ejecución y la reversa requieren --backup.")
    backup = read_backup(args.backup) if reverse else None
    report = dict(mode=args.mode, reverse=reverse, escrituras=0, propuestas=0,
                  inscripcionesModificadas=0, semanasModificadas=0)
    with psycopg2.connect("") as connection:
        connection.set_session(readonly=not writing)
        with connection.cursor() as cursor:
            lock = " FOR UPDATE" if writing else ""
            cursor.execute("SELECT to_jsonb(t) FROM app.program_templates t WHERE code=%s" + lock, (CODE,))
            found = cursor.fetchone()
            if not found:
                if reverse:
                    raise ArchiveConflict("La plantilla del respaldo ya no existe.")
                return dict(report, encontrada=False)
            current = found[0]
            cursor.execute("SELECT count(*) FROM app.program_enrollments WHERE template_id=%s", (current["id"],))
            report["inscripcionesConservadas"] = cursor.fetchone()[0]
            if reverse:
                if current == backup["before"]:
                    return dict(report, encontrada=True)
                if current != backup["after"]:
                    raise ArchiveConflict("La plantilla cambió después de archivarla; reversa rechazada.")
                desired = backup["before"]
            else:
                if current["status"] == "Archived":
                    return dict(report, encontrada=True)
                desired = deepcopy(current)
                desired["status"] = "Archived"
                backup = dict(operation=OPERATION, before=current, after=desired)
            report["propuestas"] = 1
            if writing:
                if not reverse:
                    save_backup(args.backup, backup)
                # Solo status: conserva versión, metadatos y todos los agregados del paciente.
                cursor.execute("UPDATE app.program_templates SET status=%s WHERE id=%s AND status=%s",
                               (desired["status"], current["id"], current["status"]))
                if cursor.rowcount != 1:
                    raise ArchiveConflict("Cambio concurrente; archivo revertido.")
                report["escrituras"] = 1
        connection.commit()
    return dict(report, encontrada=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--mode", choices=("simulate", "apply", "reverse"), default="simulate")
    parser.add_argument("--reverse", action="store_true", help="Simular reversa sin escribir")
    parser.add_argument("--backup", help="JSON de preimagen; obligatorio para apply/reverse")
    args = parser.parse_args()
    if args.reverse and args.mode != "simulate":
        parser.error("--reverse solo se admite con --mode simulate")
    try:
        print(json.dumps(run(args), ensure_ascii=False))
    except ArchiveConflict as error:
        print(str(error), file=sys.stderr)
        return 1
    except Exception as error:
        print(type(error).__name__ + ":" + str(getattr(error, "pgcode", "")), file=sys.stderr)
        print("Archivo interrumpido; transacción revertida. Revisar conexión/esquema sin publicar datos privados.", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
