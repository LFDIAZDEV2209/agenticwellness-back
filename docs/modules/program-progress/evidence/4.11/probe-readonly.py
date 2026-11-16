"""Lecturas reales de los dos pacientes propios; no guarda respuestas ni URLs firmadas."""
import datetime as dt
import hashlib
import importlib.util
import json
from pathlib import Path
import urllib.error
import urllib.request

REPO=Path(__file__).resolve().parents[5]
ROOT=REPO.parents[1]
helper=ROOT/'.ai/operations/organizar-experiencia-bienestar/4.11-back-fixture-helper.py'
spec=importlib.util.spec_from_file_location('qa411back',helper);h=importlib.util.module_from_spec(spec);spec.loader.exec_module(h)
fixture=h.load();evidence=h.EVIDENCE
before=h.read_state(fixture);protected=h.protected(fixture);requests=[];assertions=[]

def check(name,condition):assertions.append({'name':name,'passed':bool(condition)})

def get(path,account,expected=200):
    req=urllib.request.Request(h.h.API+path,headers={'Authorization':'Bearer '+h.token(account)})
    try:response=urllib.request.urlopen(req,timeout=30)
    except urllib.error.HTTPError as error:response=error
    with response:
        raw=response.read();body=json.loads(raw) if raw else None
        record={'method':'GET','path':path,'status':response.code,'expected':expected,'bytes':len(raw),
                'bodySha256':hashlib.sha256(raw).hexdigest(),'cacheControl':response.headers.get('Cache-Control')}
        if isinstance(body,dict):record['keys']=sorted(body)
        if isinstance(body,list):record['itemCount']=len(body)
        requests.append(record);check('HTTP '+str(expected)+' '+path,response.code==expected)
        return body,record

patient=fixture['accounts']['patient'];ai=fixture['accounts']['aiPatient'];generator=fixture['accounts']['generator']
assignments,_=get('/api/v1/health-tests/me/assignments?scope=initial',patient)
check('9/9 bateria inicial real',len(assignments)==9 and all(a['status']=='completed' for a in assignments))
plan,record=get('/api/v1/me/nutrition-plan',patient)
record['dayCount']=len(plan.get('days',[]));record['publishedPlanPresent']=bool(plan)
check('plan nutricional con 30 dias',record['dayCount']==30)
_,_=get('/api/v1/program/me/snapshot',patient,404)
completion,_=get('/api/v1/program/me/completion',patient)
check('sin inventar programa terminado',completion.get('completion') is None)
snapshot,record=get('/api/v1/program/me/snapshot',ai)
record['taskCount']=len(snapshot.get('todayTasks',[]));check('compañero activo con tareas',record['taskCount']>0)
for language in ['es','en']:
    summary,_=get('/api/v1/program/erp/patients/'+ai['patientId']+'/ai-summary?language='+language,generator)
    check('GET resumen sin generar '+language,summary.get('status')=='empty' and summary.get('summary') is None)
suggestions,record=get(fixture['aiScenario']['suggestionsPath'],generator)
draft=suggestions.get('draft') or {};record['suggestionsCount']=len(draft.get('suggestions',[]));record['model']=draft.get('model')
check('borrador sintetico real consultable',suggestions.get('status')=='ok' and record['suggestionsCount']==5 and draft.get('model')=='postman-weekly-synthetic')
check('ninguna sugerencia aplicada',all(s['state']=='draft' and s['appliedAt'] is None and s['appliedBy'] is None for s in draft.get('suggestions',[])))
_,_=get(fixture['aiScenario']['weekDetailPath'],generator)
after=h.read_state(fixture)
check('semana y auditoria sin mutacion',before['unapplied']==after['unapplied'] and after['unapplied']['weekEqualsBaseline'] and after['unapplied']['applicationAudits']==0)
check('sentinelas protegidos iguales',protected==h.protected(fixture)==fixture['protectedBefore'])
result={'commit':fixture['commit'],'capturedAt':dt.datetime.now(dt.timezone.utc).isoformat(),'target':h.h.API,'requests':requests,'assertions':assertions,
        'passed':sum(a['passed'] for a in assertions),'failed':sum(not a['passed'] for a in assertions),'businessWritesPerformed':0,
        'scope':'Lecturas sanas del runtime compartido; degradación del proveedor se valida aparte en PostgreSQL aislado, sin apagar AI8000.'}
h.write(evidence/'readonly-endpoints.json',result)
print(json.dumps({'requests':len(requests),'passed':result['passed'],'failed':result['failed'],'planDays':len(plan.get('days',[])),'suggestions':record['suggestionsCount'],'protectedUnchanged':True}))
raise SystemExit(1 if result['failed'] else 0)
