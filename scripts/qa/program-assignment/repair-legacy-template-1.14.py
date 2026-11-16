"""Reparación local de seis referencias demo de default-83w, sin migrar pacientes.

Plan -> simulación C# real -> respaldo -> apply/verify -> reverse/verify -> apply.
Solo modifica media_id de las seis filas seleccionadas; conserva el domingo válido.
"""
import argparse
import json
import pathlib
import hashlib
import importlib.util
from datetime import datetime, timezone

HERE = pathlib.Path(__file__).resolve().parent
spec = importlib.util.spec_from_file_location('repair_common', HERE / 'repair-template-1.14.py')
common = importlib.util.module_from_spec(spec)
spec.loader.exec_module(common)
CODE = 'default-83w'


def guards(db, template_id):
    queries = {
        'templateHeaders': ('SELECT to_jsonb(t) FROM app.program_templates t ORDER BY id', ()),
        'otherTemplateDays': ('SELECT to_jsonb(t) FROM app.weekly_day_templates t WHERE template_id<>%s ORDER BY id', (template_id,)),
        'otherTasks': ("SELECT to_jsonb(t) FROM app.weekly_day_templates t WHERE template_id=%s AND task_code<>'podcast' ORDER BY id", (template_id,)),
        'enrollments': ('SELECT to_jsonb(t) FROM app.program_enrollments t WHERE template_id=%s ORDER BY id', (template_id,)),
        'weeksAndIndividuals': ('SELECT to_jsonb(w) FROM app.program_weeks w JOIN app.program_enrollments e ON e.id=w.enrollment_id WHERE e.template_id=%s ORDER BY w.id', (template_id,)),
        'closedWeeks': ("SELECT to_jsonb(w) FROM app.program_weeks w WHERE status='Completed' OR completed_at IS NOT NULL ORDER BY id", ()),
    }
    result = {name: common.digest([r[0] for r in db.execute(sql, args).fetchall()]) for name, (sql, args) in queries.items()}
    for table in ['task_completions', 'daily_checkins']:
        result[table] = db.execute(f'SELECT count(*) FROM app.{table} t JOIN app.program_enrollments e ON e.id=t.enrollment_id WHERE e.template_id=%s', (template_id,)).fetchone()[0]
        assert result[table] == 0, 'Apareció historial: revisar sin aplicar'
    return result


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument('mode', choices=['plan', 'backup', 'apply', 'reverse', 'verify'])
    p.add_argument('--file', type=pathlib.Path, required=True)
    p.add_argument('--from-plan', type=pathlib.Path)
    p.add_argument('--simulation', type=pathlib.Path)
    p.add_argument('--expect', choices=['before', 'after'], default='after')
    p.add_argument('--report', type=pathlib.Path, required=True)
    a = p.parse_args()
    assert not a.report.exists(), 'No reemplazar evidencia existente'
    h = common.module('legacy_connection', common.OPS / '3.20-fixture-helper.py')
    protection = common.module('legacy_protection', common.OPS / '4.11-back-fixture-helper.py')
    protected = protection.protected(protection.load())
    # Verificación de S3 antes de abrir la transacción/bloquear filas.
    catalog = None
    if a.mode in ['plan', 'apply']:
        with h.connect() as probe:
            assert probe.info.host in ['localhost', '127.0.0.1', '::1']
            probe.autocommit = True
            catalog = common.audio_catalog(probe)
    with h.connect() as db:
        assert db.info.host in ['localhost', '127.0.0.1', '::1']
        mutating = a.mode in ['apply', 'reverse']
        db.execute("SET LOCAL lock_timeout='5s'")
        db.execute("SET LOCAL statement_timeout='60s'")
        if not mutating:
            db.execute('SET TRANSACTION ISOLATION LEVEL REPEATABLE READ, READ ONLY')
        header = db.execute('SELECT to_jsonb(t) FROM app.program_templates t WHERE code=%s' + (' FOR UPDATE' if mutating else ''), (CODE,)).fetchone()[0]
        assert header['status'] == 'Archived' and header['total_weeks'] == 83
        template_id = header['id']
        rows = common.current(db, template_id, mutating)
        sentinel = guards(db, template_id)
        writes = 0
        already_target = False
        if a.mode == 'plan':
            assert len(rows) == 7 and all(r['week_number'] is None for r in rows)
            active = db.execute("SELECT weekday,media_id FROM app.weekly_day_templates WHERE template_id=(SELECT id FROM app.program_templates WHERE code='program-coppaddresd-83-days' AND status='Active') AND task_code='podcast' AND week_number IS NULL ORDER BY weekday").fetchall()
            sources = {day: str(media) for day, media in active}
            allowed = {r['id'] for r in catalog}
            valid = {str(r[0]) for r in db.execute("SELECT id FROM app.media_items WHERE media_type='Podcast' AND status='Published' AND storage_key IS NOT NULL AND trim(storage_key)<>'' AND lower(trim(storage_key))<>'media/podcasts/demo-copp.mp3'")}
            after = []
            for row in rows:
                copy = dict(row)
                if row['media_id'] not in valid:
                    assert row['weekday'] in sources and sources[row['weekday']] in allowed
                    copy['media_id'] = sources[row['weekday']]
                after.append(copy)
            assert sum(x != y for x, y in zip(rows, after)) == 6
            assert all(r['media_id'] in allowed for r in after)
            value = dict(operation='1.14-legacy-refs', stage='proposal', capturedAt=datetime.now(timezone.utc).isoformat(),
                         templateId=template_id, templateHeader=header, beforeRows=rows, afterRows=after,
                         catalog=catalog, guards=sentinel, protected=protected, source='active template generic podcast refs; preserve valid legacy Sunday')
            common.write(a.file, value)
        else:
            path = a.from_plan if a.mode == 'backup' else a.file
            value = json.loads(path.read_text(encoding='utf-8'))
            assert value['operation'] == '1.14-legacy-refs' and value['templateId'] == template_id
            assert value['templateHeader'] == header and value['guards'] == sentinel and value['protected'] == protected, 'CAS de protección falló'
            if a.mode == 'backup':
                sim = json.loads(a.simulation.read_text(encoding='utf-8'))
                assert value['stage'] == 'proposal' and sim['canReachGlobalZero'] and sim['closedChanges'] == 0
                assert sim['proposalSha256'] == hashlib.sha256(a.from_plan.read_bytes()).hexdigest()
                assert common.digest(rows) == common.digest(value['beforeRows'])
                value.update(stage='backed-up', simulationSha256=hashlib.sha256(a.simulation.read_bytes()).hexdigest(), backedUpAt=datetime.now(timezone.utc).isoformat())
                common.write(a.file, value)
            elif a.mode == 'verify':
                assert value['stage'] == 'backed-up'
                assert common.digest(rows) == common.digest(value[a.expect+'Rows']), 'Estado inesperado'
            else:
                assert value['stage'] == 'backed-up'
                if a.mode == 'apply':
                    assert common.digest(catalog) == common.digest(value['catalog']), 'Cambió el catálogo/audio'
                    ids = [r['media_id'] for r in value['afterRows']]
                    media = db.execute("SELECT id,storage_key FROM app.media_items WHERE id=ANY(%s::uuid[]) AND status='Published' AND media_type='Podcast' FOR SHARE", (ids,)).fetchall()
                    proof = {r['id']: r['key'] for r in value['catalog']}
                    assert len(media) == 7 and all(proof[str(i)] == key for i, key in media)
                expected = value['beforeRows'] if a.mode == 'apply' else value['afterRows']
                target = value['afterRows'] if a.mode == 'apply' else value['beforeRows']
                already_target = common.digest(rows) == common.digest(target)
                assert already_target or common.digest(rows) == common.digest(expected), 'CAS de filas falló'
                if not already_target:
                    for before, after in zip(expected, target):
                        assert {k:v for k,v in before.items() if k!='media_id'} == {k:v for k,v in after.items() if k!='media_id'}
                        if before['media_id'] == after['media_id']:
                            continue
                        result = db.execute("UPDATE app.weekly_day_templates SET media_id=%s WHERE id=%s AND template_id=%s AND task_code='podcast' AND media_id IS NOT DISTINCT FROM %s", (after['media_id'], after['id'], template_id, before['media_id']))
                        assert result.rowcount == 1
                        writes += 1
                assert common.digest(common.current(db, template_id)) == common.digest(target)
                assert guards(db, template_id) == sentinel
        if mutating:
            db.commit()
        else:
            db.rollback()
    after_protected = protection.protected(protection.load())
    assert after_protected == protected
    report = dict(mode=a.mode, capturedAt=datetime.now(timezone.utc).isoformat(), templateId=template_id,
                  templateStatus='Archived', weeks=83, referenceUpdates=writes, alreadyAtTarget=already_target,
                  enrollmentsUpdated=0, weeksUpdated=0, closedDataUnchanged=True, individualsUnchanged=True,
                  allGuardsUnchanged=True, protectedBefore=protected, protectedAfter=after_protected,
                  fileSha256=hashlib.sha256(a.file.read_bytes()).hexdigest())
    common.write(a.report, report)
    print(json.dumps({k:v for k,v in report.items() if not k.startswith('protected')}))


if __name__ == '__main__':
    main()
