"""Contraparte de lectura del primer caso 9d62b7e: no repetir POST submit."""
import datetime as dt
import hashlib
import importlib.util
import json
from pathlib import Path
import urllib.error
import urllib.request

ROOT=Path('C:/Users/Luis/Documents/CoppAddresd/Repos')
OUT=Path(__file__).resolve().parent
spec=importlib.util.spec_from_file_location('qa411',ROOT/'.ai/operations/organizar-experiencia-bienestar/4.11-back-fixture-helper.py')
h=importlib.util.module_from_spec(spec);spec.loader.exec_module(h)
fixture=h.load();before=h.read_state(fixture);protected=h.protected(fixture)
with h.h.connect() as db:
    assert db.execute('SHOW transaction_read_only').fetchone()[0]=='on'
    db.rollback()
checks=[];traffic=[]
def check(name,passed):checks.append({'name':name,'passed':bool(passed)})
account=fixture['accounts']['patient'];session=h.token(account)
def get(route,expected=200):
    req=urllib.request.Request(h.h.API+route,headers={'Authorization':'Bearer '+session})
    try:response=urllib.request.urlopen(req,timeout=20)
    except urllib.error.HTTPError as error:response=error
    with response:
        raw=response.read();value=json.loads(raw) if raw else None
        traffic.append({'method':'GET','path':route,'status':response.code,'bodySha256':hashlib.sha256(raw).hexdigest()})
        check('HTTP '+str(expected)+' '+route,response.code==expected)
        return value
assignments=get('/api/v1/health-tests/me/assignments?scope=initial')
check('9 asignaciones required/completed propias',len(assignments)==9 and all(a['isRequired'] and a['status']=='completed' and a['patientId']==account['patientId'] for a in assignments))
state=before['patient'];check('9 evaluaciones',state['completedTests']==9)
check('1 inscripcion PendingAssignment semana0',len(state['enrollments'])==1 and state['enrollments'][0]['status']=='PendingAssignment' and state['enrollments'][0]['current_week_number']==0)
check('1 propuesta de nutricion rutina y7podcasts',len(state['recommendations'])==1 and bool(state['recommendations'][0]['nutritionPlan']['id']) and bool(state['recommendations'][0]['exerciseRoutine']['id']) and len(state['recommendations'][0]['podcastSequence'])==7)
with h.h.connect() as db:
    statuses=[r[0] for r in db.execute('SELECT status FROM app.program_weeks WHERE enrollment_id=%s ORDER BY week_number',(state['enrollments'][0]['id'],)).fetchall()]
    db.rollback()
check('12 semanas Locked',len(statuses)==12 and all(s=='Locked' for s in statuses))
plan=get('/api/v1/me/nutrition-plan');check('plan publicado30dias',len(plan.get('days',[]))==30 and any(p['is_published'] for p in state['nutrition']))
get('/api/v1/program/me/snapshot',404)
completion=get('/api/v1/program/me/completion');check('completion null',completion.get('completion') is None)
after=h.read_state(fixture)
check('estado propio sin cambios',before==after)
check('sentinelas historicos sin cambios',protected==h.protected(fixture)==fixture['protectedBefore'])
result={'capturedAt':dt.datetime.now(dt.timezone.utc).isoformat(),'frontTestCommit':'9d62b7e','mode':'Readonly counterpart; primer test original excluido por su POST submit replay',
        'checks':checks,'traffic':traffic,'passed':sum(c['passed'] for c in checks),'failed':sum(not c['passed'] for c in checks),
        'productMutatingRequests':0,'postSubmitReplayExecuted':False,'newTransition8To9':False,
        'scope':'Cuenta Auth real; login crea sesion. Sin solicitudes mutantes a API de producto ni SQL de escritura.'}
(OUT/'post-tests-readonly.json').write_text(json.dumps(result,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
print(json.dumps({'readRequests':len(traffic),'passed':result['passed'],'failed':result['failed'],'replayExecuted':False}))
raise SystemExit(1 if result['failed'] else 0)
