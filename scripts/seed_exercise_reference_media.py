#!/usr/bin/env python3
"""Siembra las cuatro demostraciones externas de la app y enlaza solo ejercicios sin medio.

Simula por defecto. Usa PG* del entorno, sin imprimir credenciales ni pacientes.
No descarga videos, no borra archivos y no inventa equivalencias clínicas.
"""
import argparse
from collections import Counter
from datetime import datetime, timezone
import json
from pathlib import Path
import re
import sys
import unicodedata
from urllib.parse import parse_qs, urlsplit
import uuid

import psycopg2
from psycopg2.extras import Json, RealDictCursor, execute_values


class SeedConflict(Exception):
    """Un dato existente impide una siembra segura sin decisión humana."""


def normalize(name):
    return "".join(c for c in unicodedata.normalize("NFD", name) if unicodedata.category(c) != "Mn").lower().strip()


def external_key(url):
    """Gramática de ExternalMediaRules 1.8; las pruebas contrastan las claves con el componente C#."""
    parsed = urlsplit(url)
    if parsed.scheme != "https" or parsed.username or parsed.password or parsed.fragment or parsed.port not in (None, 443):
        raise SeedConflict("URL externa inválida.")
    host = parsed.hostname; parts = parsed.path.strip("/").split("/")
    provider = video = None
    if host == "youtu.be" and len(parts) == 1:
        provider, video = "youtube", parts[0]
    elif host in ("youtube.com", "www.youtube.com", "m.youtube.com", "www.youtube-nocookie.com"):
        provider = "youtube"
        if len(parts) == 2 and parts[0] in ("embed", "shorts"):
            video = parts[1]
        elif parts == ["watch"]:
            values = parse_qs(parsed.query).get("v", [])
            if len(values) == 1:
                video = values[0]
    elif host in ("vimeo.com", "www.vimeo.com") and len(parts) == 1:
        provider, video = "vimeo", parts[0]
    elif host == "player.vimeo.com" and len(parts) == 2 and parts[0] == "video":
        provider, video = "vimeo", parts[1]
    if not video or not re.fullmatch(r"[A-Za-z0-9_-]{11}" if provider == "youtube" else r"[1-9][0-9]{0,19}", video):
        raise SeedConflict("Proveedor o identificador de video inválido.")
    return f"external/{provider}/{video}"


def catalog():
    with Path(__file__).with_name("exercise_reference_videos.json").open(encoding="utf-8") as source:
        videos = json.load(source)
    if len(videos) != 4:
        raise SeedConflict("Se esperaban las cuatro referencias editoriales de la app.")
    aliases = set(); keys = set()
    for video in videos:
        video["key"] = external_key(video["embedUrl"])
        if video["key"] in keys:
            raise SeedConflict("Referencia externa duplicada en el manifiesto.")
        keys.add(video["key"])
        for alias in video["aliases"]:
            if normalize(alias) in aliases:
                raise SeedConflict("Alias duplicado en el manifiesto.")
            aliases.add(normalize(alias))
    return videos


def run(mode):
    videos = catalog(); counts = Counter(); matched = Counter(); creating = []; linking = []
    now = datetime.now(timezone.utc)
    with psycopg2.connect("") as connection:
        connection.set_session(readonly=mode == "simulate")
        with connection.cursor(cursor_factory=RealDictCursor) as cursor:
            if mode == "apply":
                # Serializa ejecuciones de esta misma semilla sin bloquear lectores del catálogo.
                cursor.execute("SELECT pg_advisory_xact_lock(hashtext('exercise-reference-media-seed-v1'))")
            cursor.execute("SELECT id,storage_key,source_kind,external_url,media_type,status FROM app.media_items WHERE storage_key=ANY(%s)",
                           ([video["key"] for video in videos],))
            existing = {}
            for row in cursor.fetchall():
                if row["storage_key"] in existing:
                    raise SeedConflict("Hay claves externas duplicadas en el catálogo; revisar antes de sembrar.")
                existing[row["storage_key"]] = row
            by_alias = {}
            for ordinal, video in enumerate(videos, 1):
                row = existing.get(video["key"])
                if row:
                    if row["source_kind"] != "external" or row["media_type"] != "Video" or external_key(row["external_url"] or "") != video["key"]:
                        raise SeedConflict("Una clave externa existente tiene metadatos incompatibles; no se sobrescribe.")
                    media_id = str(row["id"]); counts["medios_existentes"] += 1
                    if row["status"] != "Published":
                        counts["medios_no_publicados_conservados"] += 1
                        continue
                else:
                    media_id = str(uuid.uuid5(uuid.NAMESPACE_URL, "coppaddresd:" + video["key"]))
                    creating.append((media_id, video["name"] + " · demostración", "Referencia editorial de la app. Fuente: " + video["sourceUrl"],
                                     video["author"], "Video", "SaludFisica", video["key"], "external", video["embedUrl"], "Published",
                                     ordinal, 0, 0, now, now, Json(["demostracion", "ejercicio", "referencia-app"])))
                for alias in video["aliases"]:
                    by_alias[normalize(alias)] = (video["key"], media_id)
            cursor.execute("SELECT id,name,media_id FROM app.routine_exercises")
            for exercise in cursor.fetchall():
                reference = by_alias.get(normalize(exercise["name"]))
                if not reference:
                    counts["ejercicios_sin_coincidencia"] += 1
                    continue
                key, media_id = reference; matched[key] += 1
                if exercise["media_id"] is not None:
                    counts["enlaces_ya_correctos" if str(exercise["media_id"]) == media_id else "demostraciones_configuradas_conservadas"] += 1
                else:
                    linking.append((str(exercise["id"]), media_id))
            counts["medios_propuestos"] = len(creating); counts["enlaces_propuestos"] = len(linking)
            counts["medios_sin_ejercicios_coincidentes"] = sum(not matched[video["key"]] for video in videos)
            counts["medios_creados"] = counts["enlaces_creados"] = counts["enlaces_omitidos_concurrencia"] = 0
            if mode == "apply":
                if creating:
                    execute_values(cursor, """INSERT INTO app.media_items
                      (id,title,description,author,media_type,category,storage_key,source_kind,external_url,status,sort_order,day,month,published_at,created_at,tags)
                      VALUES %s""", creating)
                    counts["medios_creados"] = cursor.rowcount
                if linking:
                    execute_values(cursor, """UPDATE app.routine_exercises e SET media_id=v.media_id::uuid,updated_at=now()
                      FROM (VALUES %s) v(id,media_id) WHERE e.id=v.id::uuid AND e.media_id IS NULL""", linking, page_size=len(linking))
                    counts["enlaces_creados"] = cursor.rowcount
                    counts["enlaces_omitidos_concurrencia"] = len(linking) - cursor.rowcount
        connection.commit()
    return dict(mode=mode, escrituras=counts["medios_creados"] + counts["enlaces_creados"], counts=dict(sorted(counts.items())))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--mode", choices=("simulate", "apply"), default="simulate")
    args = parser.parse_args()
    try:
        print(json.dumps(run(args.mode), ensure_ascii=False))
    except SeedConflict as error:
        print(str(error), file=sys.stderr)
        return 1
    except Exception as error:
        print(type(error).__name__ + ":" + str(getattr(error, "pgcode", "")), file=sys.stderr)
        print("Semilla interrumpida; transacción revertida. Revisar esquema/conexión sin publicar datos privados.", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
