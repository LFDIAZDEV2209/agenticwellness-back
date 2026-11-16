"""Operación local D: simular, respaldar, aplicar y revertir solo podcasts de la plantilla.

No publica medios ni modifica pacientes. Credenciales/conexión permanecen en helpers locales.
"""
import argparse
import hashlib
import importlib.util
import json
import pathlib
import re
import uuid
from datetime import datetime, timezone

ROOT = pathlib.Path(__file__).resolve().parents[5]
OPS = ROOT / '.ai/operations/organizar-experiencia-bienestar'
CODE = 'program-coppaddresd-83-days'
EPISODES = [1, 2, 3, 4, 5, 6, 7, 14, 15, 16, 17, 18, 19]
BUCKET = 'cooppadresd-storage-prod'


def module(name, path):
    spec = importlib.util.spec_from_file_location(name, path)
    value = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(value)
    return value


def digest(value):
    return hashlib.sha256(json.dumps(value, sort_keys=True, separators=(',', ':'), default=str).encode()).hexdigest()


def write(path, value):
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open('x', encoding='utf-8', newline='\n') as stream:
        json.dump(value, stream, ensure_ascii=False, indent=2, default=str)
        stream.write('\n')


def rows(db, template_id, lock=False):
    sql = "SELECT to_jsonb(t) FROM app.weekly_day_templates t WHERE template_id=%s AND task_code='podcast' ORDER BY week_number NULLS FIRST,weekday,id"
    return db.execute(sql + (' FOR UPDATE' if lock else ''), (template_id,)).fetchall()


def current(db, template_id, lock=False):
    result = [r[0] for r in rows(db, template_id, lock)]
    for row in result:
        row['created_at'] = datetime.fromisoformat(row['created_at']).isoformat()
    return result


def sentinels(db, template_id):
    queries = {
        'nonPodcast': ("SELECT to_jsonb(t) FROM app.weekly_day_templates t WHERE template_id=%s AND task_code<>'podcast' ORDER BY id", (template_id,)),
        'otherTemplates': ('SELECT to_jsonb(t) FROM app.weekly_day_templates t WHERE template_id<>%s ORDER BY id', (template_id,)),
        'templateHeaders': ('SELECT to_jsonb(t) FROM app.program_templates t ORDER BY id', ()),
        # El stack sigue vivo: el CAS protege cierres, sin exigir inmovilidad de otros QA activos.
        'closedWeeks': ("SELECT to_jsonb(t) FROM app.program_weeks t WHERE status='Completed' OR completed_at IS NOT NULL ORDER BY id", ()),
    }
    return {key: digest([r[0] for r in db.execute(sql, args).fetchall()]) for key, (sql, args) in queries.items()}


def audio_catalog(db):
    import boto3
    s3 = boto3.Session(profile_name='default', region_name='us-east-2').client('s3')
    candidates = {}
    for media_id, title, key in db.execute("SELECT id,title,storage_key FROM app.media_items WHERE media_type='Podcast' AND status='Published'"):
        match = re.match(r'^Ep\.\s*(\d+)\s*:', title)
        if match and int(match[1]) in EPISODES:
            episode = int(match[1])
            assert episode not in candidates and key and 'demo' not in key.lower()
            head = s3.head_object(Bucket=BUCKET, Key=key)
            body = s3.get_object(Bucket=BUCKET, Key=key, Range='bytes=0-4095')['Body']
            try:
                prefix = body.read()
            finally:
                body.close()
            signature = prefix.startswith(b'ID3') or any(prefix[i] == 255 and prefix[i+1] & 224 == 224 for i in range(min(len(prefix)-1, 1024)))
            assert head['ContentLength'] > 4096 and signature, 'Audio inválido'
            assert head['ContentType'].split(';')[0] in ['audio/mpeg', 'audio/mp3', 'application/octet-stream']
            candidates[episode] = dict(id=str(media_id), episode=episode, key=key, bytes=head['ContentLength'], contentType=head['ContentType'], signatureValid=True, prefixSha256=hashlib.sha256(prefix).hexdigest())
    assert sorted(candidates) == EPISODES
    return [candidates[e] for e in EPISODES]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('mode', choices=['simulate', 'apply', 'reverse', 'verify'])
    parser.add_argument('--backup', type=pathlib.Path, required=True)
    parser.add_argument('--report', type=pathlib.Path, required=True)
    args = parser.parse_args()
    h = module('repair_connection', OPS / '3.20-fixture-helper.py')
    protection = module('repair_protection', OPS / '4.11-back-fixture-helper.py')
    protected = protection.protected(protection.load())
    with h.connect() as db:
        # El servidor puede verse con IP de contenedor; el destino del cliente debe ser loopback.
        assert db.info.host in ['localhost', '127.0.0.1', '::1']
        db.execute("SET LOCAL lock_timeout='5s'")
        db.execute("SET LOCAL statement_timeout='120s'")
        if args.mode in ['simulate', 'verify']:
            db.execute('SET TRANSACTION READ ONLY')
        header = db.execute('SELECT to_jsonb(t) FROM app.program_templates t WHERE code=%s AND status=\'Active\'' + (' FOR UPDATE' if args.mode in ['apply', 'reverse'] else ''), (CODE,)).fetchone()[0]
        template_id = header['id']
        before = current(db, template_id, args.mode in ['apply', 'reverse'])
        guards = sentinels(db, template_id)
        if args.mode == 'simulate':
            assert len(before) == 7 and all(r['week_number'] is None for r in before)
            catalog = audio_catalog(db)
            planned = []
            for row in before:
                planned.append(dict(row, media_id=catalog[row['weekday']-1]['id']))
            for day in range(83):
                source = before[day % 7]
                planned.append(dict(source, id=str(uuid.uuid4()), week_number=day//7+1, weekday=day%7+1,
                                    media_id=catalog[day % len(catalog)]['id'], created_by=None,
                                    created_at=datetime.now(timezone.utc).isoformat()))
            planned.sort(key=lambda r: (r['week_number'] or 0, r['weekday'], r['id']))
            backup = dict(operation='1.14-D', templateId=template_id, code=CODE, capturedAt=datetime.now(timezone.utc).isoformat(),
                          beforeRows=before, afterRows=planned, guards=guards, protected=protected, catalog=catalog,
                          rotationDays=13, c1Exception='Base C1 de 42 publicados reducida temporalmente a 13 audios reales; catálogo de 37 con 24 mp3 pendientes')
            write(args.backup, backup)
            assert args.backup.is_file()
            changed = 0
        else:
            backup = json.loads(args.backup.read_text(encoding='utf-8'))
            assert backup['operation'] == '1.14-D' and backup['templateId'] == template_id
            assert guards == backup['guards'] and protected == backup['protected'], 'Cambió un sentinela; revisar antes de continuar'
            expected = backup['afterRows'] if args.mode in ['reverse', 'verify'] else backup['beforeRows']
            assert digest(before) == digest(expected), 'CAS: el contenido cambió; no sobrescribir'
            target = backup['beforeRows'] if args.mode == 'reverse' else backup['afterRows']
            if args.mode in ['apply', 'reverse']:
                catalog = audio_catalog(db) if args.mode == 'apply' else backup['catalog']
                assert digest(catalog) == digest(backup['catalog']), 'El catálogo/audio cambió'
                db.execute("DELETE FROM app.weekly_day_templates WHERE template_id=%s AND task_code='podcast'", (template_id,))
                db.execute('INSERT INTO app.weekly_day_templates SELECT * FROM jsonb_populate_recordset(NULL::app.weekly_day_templates,%s::jsonb)', (json.dumps(target),))
                # Normalizar timestamps por PostgreSQL antes de comparar (mismo instante/valor).
                observed = current(db, template_id)
                assert len(observed) == len(target)
                for a, b in zip(observed, target):
                    assert {k:v for k,v in a.items() if k != 'created_at'} == {k:v for k,v in b.items() if k != 'created_at'}
                    assert datetime.fromisoformat(a['created_at']) == datetime.fromisoformat(b['created_at'])
                assert sentinels(db, template_id) == guards
                changed = len(before) + len(target)
            else:
                changed = 0
        # Comparar backups futuros contra la serialización canónica de PostgreSQL.
        # jsonb usa +00:00 igual que datetime; simulate conserva las filas originales intactas.
        report = dict(mode=args.mode, templateId=template_id, specificCells=83, genericFallbacks=7,
                      catalogCount=13, rotationDays=13, businessRowsOutsideTemplate=0,
                      podcastRowOperations=changed, guardsUnchanged=sentinels(db, template_id)==guards,
                      backupSha256=hashlib.sha256(args.backup.read_bytes()).hexdigest(), protectedBefore=protected)
        if args.mode in ['apply', 'reverse']:
            db.commit()
        else:
            db.rollback()
    report['protectedAfter'] = protection.protected(protection.load())
    assert report['protectedAfter'] == protected
    write(args.report, report)
    print(json.dumps({k:v for k,v in report.items() if not k.startswith('protected')}))


if __name__ == '__main__':
    main()
