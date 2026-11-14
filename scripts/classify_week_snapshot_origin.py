#!/usr/bin/env python3
"""Clasifica snapshots futuros. Por defecto simula; usa PG* sin imprimir credenciales.

Requiere psycopg2. --mode apply/reverse son acciones explícitas; reverse también
puede simularse con --mode simulate --reverse. --fill-legacy ejecuta el relleno
2.10 de asignaciones vigentes, con preimagen por celda y reversa independiente.
"""
import argparse
from collections import Counter
from copy import deepcopy
from datetime import date, timedelta
import json
import sys

import psycopg2
from psycopg2.extras import RealDictCursor, execute_values

VERSION = 1
MARKER = "_origin_backfill_v1"
FIELDS = ("weekday", "task_code", "points", "sort_order", "routine_id", "nutrition_plan_id", "media_id")
LEGACY_MARKER = "_legacy_backfill_v2"


def fill_legacy(week, templates, assignments, reverse, counts):
    """Solo copia referencias inequívocas; preserva individuales, descansos y contenido no relacionado."""
    original = week["tasks_snapshot"]
    if not isinstance(original, list) or any(not isinstance(row, dict) for row in original):
        counts["conflicto_snapshot_invalido"] += 1
        return original
    keys = [(row.get("weekday"), row.get("task_code")) for row in original]
    if len(set(keys)) != len(keys):
        counts["conflicto_snapshot_duplicado"] += 1
        return original
    result = deepcopy(original)
    if reverse:
        restored = []
        for row in result:
            backup = row.get(LEGACY_MARKER)
            if not isinstance(backup, dict):
                restored.append(row)
                continue
            if {key: value for key, value in row.items() if key != LEGACY_MARKER} != backup.get("applied"):
                counts["conflicto_editado_despues"] += 1
                restored.append(row)
                continue
            if backup.get("before") is not None:
                restored.append(backup["before"])
            counts["items_legacy_revertidos"] += 1
        return restored
    template = templates.get(str(week["template_id"]))
    if not template:
        counts["conflicto_sin_plantilla"] += 1
        return original
    duration = 83 if template["total_weeks"] == 12 else template["total_weeks"] * 7
    last = week["enrollment_start"] + timedelta(days=duration - 1)
    for offset in range(7):
        local_date = week["effective_start"] + timedelta(days=offset)
        if local_date > last:
            break
        weekday = local_date.isoweekday()
        for code, field in (("ejercicio", "routine_id"), ("nut", "nutrition_plan_id")):
            prior = next((row for row in result if (row.get("weekday"), row.get("task_code")) == (weekday, code)), None)
            if prior and (prior.get("origin") != "program" or prior.get("rest") is True or LEGACY_MARKER in prior):
                counts["items_individuales_conservados"] += 1
                continue
            candidates = [a for a in assignments.get((str(week["patient_id"]), code), [])
                          if a["start_date"] <= local_date and (a["end_date"] is None or a["end_date"] >= local_date)]
            invalid = [a for a in candidates if not a["valid"]]
            counts["asignaciones_invalidas_omitidas"] += len(invalid)
            candidates = [a for a in candidates if a["valid"]]
            ids = {a["content_id"] for a in candidates}
            if len(ids) > 1:
                counts["conflicto_asignaciones_solapadas"] += 1
                continue
            if not candidates:
                continue
            source = max(candidates, key=lambda a: (a["start_date"], a["created_at"], a["content_id"]))
            defaults = [row for row in template["days"] if row["weekday"] == weekday and row["task_code"] == code
                        and row["week_number"] in (None, week["week_number"])]
            defaults.sort(key=lambda row: row["week_number"] is not None, reverse=True)
            base = prior or (defaults[0] if defaults else None)
            if base is None:
                counts["conflicto_sin_puntos_plantilla"] += 1
                continue
            row = {key: base.get(key) for key in FIELDS}
            row.update({field: source["content_id"], "origin": "individual", "rest": False,
                        "edited_by": source["created_by"], "edited_at": source["created_at"].isoformat(), "backfill_version": 2})
            row[LEGACY_MARKER] = {"before": deepcopy(prior), "applied": deepcopy(row)}
            if prior is not None:
                result[result.index(prior)] = row
            else:
                result.append(row)
            counts["items_legacy_copiados"] += 1
    return result


def classify(item, week, templates):
    """La ausencia de prueba histórica nunca autoriza convertir en programa."""
    code = item.get("task_code")
    if (code == "ejercicio" and week["legacy_routine"]) or (code == "nut" and week["legacy_nutrition"]):
        return "individual", "legacy_humano_o_ambiguo"
    template = templates.get(str(week["template_id"]))
    if not template or not week["template_version_at_start"] or template["version"] != week["template_version_at_start"]:
        return "individual", "sin_version_historica"
    # El catálogo no conserva revisiones antiguas. Si se editó después de crear
    # la semana, ni siquiera la igualdad de versión prueba el contenido original.
    if template["updated_at"] and template["updated_at"] > week["created_at"]:
        return "individual", "plantilla_editada_sin_revision_historica"
    matches = [row for row in template["days"] if row["weekday"] == item.get("weekday")
               and row["task_code"] == code and row["week_number"] in (None, week["week_number"])]
    matches.sort(key=lambda row: row["week_number"] is not None, reverse=True)
    if not matches or any(key not in item for key in FIELDS[:4]) or item.get("rest") is True:
        return "individual", "no_comparable"
    return ("program", "igual_plantilla") if all(item.get(key) == matches[0].get(key) for key in FIELDS) else ("individual", "distinto_plantilla")


def transform(week, templates, reverse, counts):
    original = week["tasks_snapshot"]
    if not isinstance(original, list):
        counts["conflicto_snapshot_no_array"] += 1
        return original
    if any(not isinstance(item, dict) or not isinstance(item.get("weekday"), int) or not isinstance(item.get("task_code"), str) for item in original):
        counts["conflicto_items_invalidos_o_duplicados"] += 1
        return original
    if not original:
        counts["semanas_vacias"] += 1
    keys = [(item.get("weekday"), item.get("task_code")) for item in original if isinstance(item, dict)]
    if len(keys) != len(original) or len(set(keys)) != len(keys):
        counts["conflicto_items_invalidos_o_duplicados"] += 1
        return original
    result = deepcopy(original)
    for index, item in enumerate(result):
        if reverse:
            backup = item.get(MARKER)
            if not isinstance(backup, dict) or item.get("backfill_version") != VERSION:
                counts["items_sin_reversa"] += 1
                continue
            before = backup.get("before")
            if not isinstance(before, dict):
                counts["conflicto_reversa_invalida"] += 1
                continue
            expected = dict(before, origin=backup.get("origin"), backfill_version=VERSION)
            if {key: value for key, value in item.items() if key != MARKER} != expected:
                counts["conflicto_editado_despues"] += 1
                continue
            result[index] = before
            counts["items_revertidos"] += 1
        else:
            if item.get("origin") or item.get("backfill_version") is not None or MARKER in item:
                counts["items_ya_clasificados"] += 1
                continue
            origin, reason = classify(item, week, templates)
            before = deepcopy(item)
            item.update(origin=origin, backfill_version=VERSION)
            item[MARKER] = {"before": before, "origin": origin}
            counts["items_" + origin] += 1
            counts["motivo_" + reason] += 1
            if origin == "individual" and reason not in ("distinto_plantilla",):
                counts["conflictos_conservados_individual"] += 1
    return result


def run(args):
    counts = Counter({key: 0 for key in ("semanas_con_cambios", "semanas_escritas", "items_program", "items_individual", "conflictos_conservados_individual", "conflicto_concurrencia")})
    with psycopg2.connect("") as connection:
        simulation = args.mode == "simulate"
        connection.set_session(readonly=simulation)
        with connection.cursor(cursor_factory=RealDictCursor) as cursor:
            cursor.execute("SET statement_timeout = '60s'; SET lock_timeout = '5s'")
            cursor.execute("SELECT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema='app' AND table_name='weekly_day_templates' AND column_name='week_number') AS present")
            week_column = "week_number" if cursor.fetchone()["present"] else "NULL::integer"
            cursor.execute("SELECT id::text, version, total_weeks, updated_at FROM app.program_templates")
            templates = {row["id"]: dict(row, days=[]) for row in cursor.fetchall()}
            cursor.execute(f"SELECT template_id::text, {week_column} AS week_number, weekday, task_code, points, sort_order, routine_id::text, nutrition_plan_id::text, media_id::text FROM app.weekly_day_templates")
            for row in cursor.fetchall():
                if row["template_id"] in templates:
                    templates[row["template_id"]]["days"].append(row)
            cursor.execute("SELECT count(*) AS total FROM app.program_weeks")
            counts["semanas_totales"] = cursor.fetchone()["total"]
        connection.commit()
        after = "00000000-0000-0000-0000-000000000000"
        # El relleno usa el calendario canónico de la inscripción (2.8-fix).
        week_start_sql = "e.start_local_date+((w.week_number-1)*7)" if args.fill_legacy else "w.week_start_date_local"
        while True:
            with connection.cursor(cursor_factory=RealDictCursor) as cursor:
                cursor.execute(f"""
                    SELECT w.*, e.template_id, e.patient_id, e.start_local_date AS enrollment_start, {week_start_sql} AS effective_start,
                      EXISTS (SELECT 1 FROM app.routine_assignments a WHERE a.patient_id=e.patient_id
                        AND a.start_date <= w.week_end_date_local AND (a.end_date IS NULL OR a.end_date >= w.week_start_date_local)) AS legacy_routine,
                      EXISTS (SELECT 1 FROM app.nutrition_plan_assignments a WHERE a.patient_id=e.patient_id
                        AND a.start_date <= w.week_end_date_local AND (a.end_date IS NULL OR a.end_date >= w.week_start_date_local)) AS legacy_nutrition
                    FROM app.program_weeks w JOIN app.program_enrollments e ON e.id=w.enrollment_id
                    WHERE w.id > %s::uuid AND w.status='Locked' AND w.activated_at IS NULL AND w.completed_at IS NULL
                      AND NOT EXISTS (SELECT 1 FROM app.task_completions tc WHERE tc.program_week_id=w.id)
                      AND {week_start_sql} > COALESCE(%s::date, (CURRENT_TIMESTAMP AT TIME ZONE e.timezone)::date)
                    ORDER BY w.id LIMIT %s
                    """, (after, args.today, args.batch_size))
                weeks = cursor.fetchall()
                if not weeks:
                    break
                assignments = {}
                if args.fill_legacy and not (args.reverse or args.mode == "reverse"):
                    patient_ids = list({str(week["patient_id"]) for week in weeks})
                    for table, content, catalog, code, owner_check in (
                        ("routine_assignments", "routine_id", "exercise_routines", "ejercicio", "TRUE"),
                        ("nutrition_plan_assignments", "plan_id", "nutrition_plans", "nut", "(c.patient_id IS NULL OR c.patient_id=a.patient_id)")):
                        # Identificadores constantes internos; todos los datos se parametrizan.
                        cursor.execute(f"""SELECT a.patient_id::text, a.{content}::text AS content_id,
                            a.start_date,a.end_date,a.created_by::text,a.created_at,
                            (c.status::text IN ('Active','2') AND {owner_check}) AS valid
                            FROM app.{table} a JOIN app.{catalog} c ON c.id=a.{content}
                            WHERE a.patient_id=ANY(%s::uuid[]) AND a.status::text IN ('Active','1')""", (patient_ids,))
                        for assignment in cursor.fetchall():
                            assignments.setdefault((assignment["patient_id"], code), []).append(assignment)
                updates = []
                for week in weeks:
                    counts["semanas_futuras_evaluadas"] += 1
                    changed = (fill_legacy(week, templates, assignments, args.reverse or args.mode == "reverse", counts)
                               if args.fill_legacy else transform(week, templates, args.reverse or args.mode == "reverse", counts))
                    if changed == week["tasks_snapshot"]:
                        continue
                    counts["semanas_con_cambios"] += 1
                    if not simulation:
                        # Comparación optimista y estado/fecha revalidados en la escritura:
                        # una activación o edición concurrente nunca se sobrescribe.
                        updates.append((json.dumps(changed), str(week["id"]), json.dumps(week["tasks_snapshot"])))
                if updates:
                    execute_values(cursor, f"""
                        UPDATE app.program_weeks w SET tasks_snapshot=v.snapshot::jsonb
                        FROM app.program_enrollments e, (VALUES %s) AS v(snapshot,id,original)
                        WHERE e.id=w.enrollment_id AND w.id=v.id::uuid
                          AND w.tasks_snapshot=v.original::jsonb AND w.status='Locked'
                          AND w.activated_at IS NULL AND w.completed_at IS NULL
                          AND NOT EXISTS (SELECT 1 FROM app.task_completions tc WHERE tc.program_week_id=w.id)
                          AND {week_start_sql} > (CURRENT_TIMESTAMP AT TIME ZONE e.timezone)::date
                        """, updates, page_size=len(updates))
                    counts["semanas_escritas"] += cursor.rowcount
                    counts["conflicto_concurrencia"] += len(updates) - cursor.rowcount
                after = str(weeks[-1]["id"])
            connection.commit()
    counts["semanas_excluidas_por_estado_o_fecha"] = counts["semanas_totales"] - counts["semanas_futuras_evaluadas"]
    return dict(mode=args.mode, reverse=args.reverse or args.mode == "reverse", version=2 if args.fill_legacy else VERSION,
                escrituras=counts["semanas_escritas"], counts=dict(sorted(counts.items())))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--mode", choices=("simulate", "apply", "reverse"), default="simulate")
    parser.add_argument("--reverse", action="store_true", help="Simular reversa sin escribir")
    parser.add_argument("--fill-legacy", action="store_true", help="2.10: copiar asignaciones vigentes a ítems individuales; reversa independiente")
    parser.add_argument("--today", type=date.fromisoformat, help="Fecha reproducible para pruebas; omitir en operación")
    parser.add_argument("--batch-size", type=int, default=500)
    args = parser.parse_args()
    if not 1 <= args.batch_size <= 5000 or (args.reverse and args.mode != "simulate"):
        parser.error("Lote 1..5000; --reverse solo con simulate")
    try:
        print(json.dumps(run(args), ensure_ascii=False))
    except Exception as error:
        # No volcar excepciones del driver: pueden incluir DSN o datos clínicos.
        print(type(error).__name__ + ":" + str(getattr(error, "pgcode", "")), file=sys.stderr)
        print("Clasificación interrumpida; lote actual revertido. Revisar conexión/esquema sin publicar datos privados.", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
