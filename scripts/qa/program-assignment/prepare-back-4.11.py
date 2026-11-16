"""Soporte BACK 4.11. Solo pacientes QA propios; no resetear ni aplicar sugerencias.

prepare deja 8/9; complete-tests consume una vez el último test propio.
generate-unapplied guarda un borrador, nunca ejecuta /apply. check es lectura.
Contraseñas/tokens solo en memoria y Windows Credential Manager.
"""
import argparse
import datetime as dt
import hashlib
import importlib.util
import json
from pathlib import Path
import re
import secrets
import subprocess
import os
import time
import uuid

REPO=Path(__file__).resolve().parents[3]
ROOT=REPO.parents[1]
HERE=ROOT/'.ai/operations/organizar-experiencia-bienestar'
META=HERE/'4.11-back-fixture.json'
EVIDENCE=ROOT/'openspec/changes/organizar-experiencia-bienestar/revision-4.11-back'
FORBIDDEN={'0c487f75-a462-4fda-894d-61c031441884','ef59c692-d01a-4105-b3ea-05c07ff76279',
           '0d71891f-0ffe-464d-b8de-0796d27bcea5','f9153387-3bbb-44eb-97cc-5110e9ee2d76'}

def module(name,path):
    spec=importlib.util.spec_from_file_location(name,path)
    result=importlib.util.module_from_spec(spec);spec.loader.exec_module(result);return result
h=module('qa_shared',HERE/'3.20-fixture-helper.py')
h8=module('qa48',HERE/'4.8-fixture-helper.py')
app=module('qa411_app_source',ROOT/'.worktrees/app-f3/scripts/qa/program-assignment/prepare-fixture.py')

def write(path,value):
    path.parent.mkdir(parents=True,exist_ok=True)
    path.write_text(json.dumps(value,ensure_ascii=False,indent=2,default=str)+'\n',encoding='utf-8')

def digest(value):
    return hashlib.sha256(json.dumps(value,sort_keys=True,separators=(',',':'),default=str).encode()).hexdigest()

def load():
    value=json.loads(META.read_text(encoding='utf-8'))
    assert value['owner']=='codex-back'
    for key in ['patient','aiPatient']:
        account=value['accounts'][key]
        assert account['patientId'] not in FORBIDDEN and account['login'].startswith('qa411back-')
        assert account['credentialTarget'].startswith('CoppAddresd/QA/4.11-back/')
    return value

def token(account):
    session=h.login(account);return session.get('accessToken') or session['token']

def api(method,path,account,body=None):
    # El helper compartido omite cuerpos de error para no volcar PHI/secretos.
    return h.request(method,h.API+path,body,token=token(account))

def admin():
    text=(ROOT/'coppAddresdBack/src/Services/CoppAddresd.Auth/appsettings.json').read_text(encoding='utf-8-sig')
    def field(name):
        found=re.search('"'+name+'"'+r'\s*:\s*"([^"\r\n]+)"',text)
        assert found,'Falta configuracion Auth local';return found.group(1)
    session=h.request('POST',h.AUTH+'/api/auth/login',{'email':field('AdminEmail'),'password':field('AdminPassword'),'application':'erp','rememberMe':False})
    return session.get('accessToken') or session['token']

def protected(value):
    own=[value['accounts'][key]['patientId'] for key in ['patient','aiPatient']]
    result={'closed320AndR7':h8.protected_snapshot(),
            'parkedAppFixtureFileSha256':hashlib.sha256((HERE/'4.11-app-fixture.json').read_bytes()).hexdigest()}
    with h.connect() as db:
        db.execute('SET TRANSACTION ISOLATION LEVEL REPEATABLE READ, READ ONLY')
        # Todos los planes/cierres ajenos a los dos pacientes nuevos son sentinelas.
        queries={
          'publishedPlans':('SELECT to_jsonb(t)::text FROM app.nutrition_prescriptions t WHERE is_published AND NOT(patient_id=ANY(%s::uuid[])) ORDER BY id',own),
          'publishedReviews':('SELECT to_jsonb(t)::text FROM app.nutrition_cycle_reviews t WHERE published_at IS NOT NULL AND cycle_id IN (SELECT id FROM app.nutrition_cycles WHERE NOT(patient_id=ANY(%s::uuid[]))) ORDER BY id',own),
          'parkedEnrollments':('SELECT to_jsonb(t)::text FROM app.program_enrollments t WHERE patient_id=ANY(%s::uuid[]) ORDER BY id',['0d71891f-0ffe-464d-b8de-0796d27bcea5','f9153387-3bbb-44eb-97cc-5110e9ee2d76']),
          'parkedEvaluations':('SELECT to_jsonb(t)::text FROM app.health_test_evaluations t WHERE patient_id=ANY(%s::uuid[]) ORDER BY id',['0d71891f-0ffe-464d-b8de-0796d27bcea5','f9153387-3bbb-44eb-97cc-5110e9ee2d76'])}
        # Fijar las filas cerradas del corte inicial permite altas concurrentes ajenas.
        # Sus hashes baseline se conservan; no se ocultan cambios de las filas originales.
        if value.get('protectedPublishedPlanIds'):
            queries['publishedPlans']=('SELECT to_jsonb(t)::text FROM app.nutrition_prescriptions t WHERE id=ANY(%s::uuid[]) ORDER BY id',value['protectedPublishedPlanIds'])
        if value.get('protectedPublishedReviewIds'):
            queries['publishedReviews']=('SELECT to_jsonb(t)::text FROM app.nutrition_cycle_reviews t WHERE id=ANY(%s::uuid[]) ORDER BY id',value['protectedPublishedReviewIds'])
        for key,(query,ids) in queries.items():
            rows=[r[0] for r in db.execute(query,(ids,)).fetchall()]
            result[key]={'count':len(rows),'sha256':hashlib.sha256('\n'.join(rows).encode()).hexdigest()}
        db.rollback()
    return result

def seed_profile(value,key):
    account=value['accounts'][key]
    if not account.get('userId'):
        credential=h.read_credential(account['credentialTarget'])
        created=h.request('POST',h.AUTH+'/api/auth/users',{'email':credential['login'],'password':credential['password'],'firstName':'QA411BACK','lastName':key})
        account['userId']=created['id'];write(META,value)
    if not account.get('profilePrepared'):
        # Únicas escrituras SQL: acceso APP/perfil del UUID nuevo; no copiar tests/resultados.
        with h.connect() as db:
            exists=db.execute('SELECT id FROM app.patient_profiles WHERE id=%s OR user_id=%s',(account['patientId'],account['userId'])).fetchall()
            assert not exists,'Perfil ya existente: detener y revisar, nunca reemplazar'
            db.execute('INSERT INTO auth."UserApplications"("UserId","ApplicationId","CreatedAt","IsSuspended","SessionVersion") SELECT %s,"Id",now(),false,0 FROM auth."Applications" WHERE "Code"=\'app\' ON CONFLICT DO NOTHING',(account['userId'],))
            db.execute("INSERT INTO app.patient_profiles(id,user_id,first_name,last_name,status,timezone,document_number,email,date_of_birth,gender,league_opt_in) VALUES(%s,%s,'QA411BACK',%s,'Activo','America/Bogota',%s,%s,DATE '1986-01-01','Femenino',false)",(account['patientId'],account['userId'],key,account['documentNumber'],account['login']))
        account['profilePrepared']=True;write(META,value)

def read_state(value):
    result={}
    with h.connect() as db:
        db.execute('SET TRANSACTION ISOLATION LEVEL REPEATABLE READ, READ ONLY')
        for key in ['patient','aiPatient']:
            patient=value['accounts'][key]['patientId']
            evaluations=db.execute("SELECT count(*) FROM app.health_test_evaluations WHERE patient_id=%s AND status='completed'",(patient,)).fetchone()[0]
            enrollments=[r[0] for r in db.execute('SELECT to_jsonb(t) FROM app.program_enrollments t WHERE patient_id=%s ORDER BY id',(patient,)).fetchall()]
            recs=[r[0] for r in db.execute('SELECT to_jsonb(t) FROM app.program_assignment_recommendations t WHERE patient_id=%s ORDER BY id',(patient,)).fetchall()]
            prescriptions=[r[0] for r in db.execute('SELECT to_jsonb(t) FROM app.nutrition_prescriptions t WHERE patient_id=%s ORDER BY id',(patient,)).fetchall()]
            rows={'completedTests':evaluations,'enrollments':[{k:r.get(k) for k in ['id','status','current_week_number','start_local_date','template_id']} for r in enrollments],
                  'nutrition':[{k:r.get(k) for k in ['id','state','is_published','start_date','end_date','narrative_source']} for r in prescriptions],
                  'recommendations':[]}
            for rec in recs:
                proposal=rec['proposal'];proposal=json.loads(proposal) if isinstance(proposal,str) else proposal
                rows['recommendations'].append({'id':rec['id'],'state':rec['state'],'proposalSha256':digest(proposal),
                    'profileSnapshotSha256':digest(rec['profile_snapshot']),
                    'proposalKeys':sorted(proposal),'nutritionPlan':proposal.get('nutritionPlan'),
                    'exerciseRoutine':proposal.get('routine'),'podcastSequence':proposal.get('podcastSequence'),
                    'createdAt':rec.get('created_at')})
            result[key]=rows
        if value.get('aiScenario'):
            scenario=value['aiScenario'];enrollment=scenario['enrollmentId'];week=scenario['weekNumber']
            row=db.execute('SELECT to_jsonb(w) FROM app.program_weeks w WHERE enrollment_id=%s AND week_number=%s',(enrollment,week)).fetchone()[0]
            audits=db.execute("SELECT count(*) FROM audit.activity_logs WHERE (record_id::text=%s OR record_id::text=%s) AND coalesce(metadata::text,'') LIKE %s",(str(row['id']),enrollment,'%"ai_application"%')).fetchone()[0]
            briefs=[{'id':r[0],'state':r[1]} for r in db.execute("SELECT id,state FROM app.patient_ai_briefs WHERE patient_id=%s AND kind='weekly_plan_suggestions' ORDER BY id",(value['accounts']['aiPatient']['patientId'],)).fetchall()]
            result['unapplied']={'weekSha256':digest(row),'weekEqualsBaseline':digest(row)==scenario['weekSha256Before'],'applicationAudits':audits,'briefs':briefs}
        db.rollback()
    return result

def prepare():
    if META.exists():
        value=load()
        assert value['stage'] in ['preparing','ready-8-of-9'],'No resetear fixture consumida'
    else:
        run=dt.datetime.now().strftime('%Y%m%d')+'-'+uuid.uuid4().hex[:8]
        value={'run':run,'owner':'codex-back','commit':'e0a9c29dfbf551eaa3d51a97173fe72b55a0781b','stage':'preparing','apiBaseUrl':h.API,'authBaseUrl':h.AUTH,'accounts':{},'submissions':[],'synthetic':True}
        for key in ['patient','aiPatient']:
            login='qa411back-'+run+'-'+key.lower()+'@qa.coppaddresd.test';target='CoppAddresd/QA/4.11-back/'+run+'/'+key
            h.write_credential(target,login,'Qa!9'+secrets.token_urlsafe(28))
            value['accounts'][key]={'login':login,'application':'app','credentialTarget':target,'patientId':str(uuid.uuid4()),'documentNumber':'4119'+str(secrets.randbelow(10**9)).zfill(9)}
        with h.connect() as db:
            db.execute('SET TRANSACTION READ ONLY')
            value['protectedPublishedPlanIds']=[str(r[0]) for r in db.execute('SELECT id FROM app.nutrition_prescriptions WHERE is_published ORDER BY id').fetchall()]
            value['protectedPublishedReviewIds']=[str(r[0]) for r in db.execute('SELECT id FROM app.nutrition_cycle_reviews WHERE published_at IS NOT NULL ORDER BY id').fetchall()]
            db.rollback()
        value['protectedBefore']=protected(value);write(META,value)
    for key in ['patient','aiPatient']:seed_profile(value,key)
    account=value['accounts']['patient'];session=token(account)
    assignments=h.request('GET',h.API+'/api/v1/health-tests/me/assignments?scope=initial',token=session)
    assert len(assignments)==9 and len({a['batteryAssignmentId'] for a in assignments})==1
    final=next(a for a in assignments if a['testCode']=='ers')
    for assignment in assignments:
        path='/api/v1/health-tests/me/tests/'+assignment['id']
        detail=h.request('GET',h.API+path,token=session)
        answers,choices=app.answers_for(detail)
        payload={'answers':answers,'clientRequestId':'qa411back-'+assignment['id']}
        if assignment['id']==final['id']:
            assert assignment['status']=='pending','No repetir ni resetear ultimo test'
            value['lastTest']={'assignmentId':assignment['id'],'testCode':assignment['testCode'],'testName':assignment['testName'],'choices':choices,'submitPayload':payload}
            write(META,value);continue
        if assignment['status']!='completed':
            h.request('POST',h.API+path+'/start',token=session)
            result=h.request('POST',h.API+path+'/submit',payload,token=session)
            assert result['status']=='completed'
            value['submissions'].append({'assignmentId':assignment['id'],'testCode':assignment['testCode'],'evaluationId':result['id']});write(META,value)
    generator=json.loads((HERE/'4.8-fixture.json').read_text(encoding='utf-8'))['accounts']['generator']
    value['accounts']['generator']={k:generator[k] for k in ['login','application','credentialTarget','userId','professionalId']}
    adm=admin()
    for key in ['patient','aiPatient']:
        pid=value['accounts'][key]['patientId']
        assignments=h.request('GET',h.API+'/api/v1/patients/'+pid+'/professionals',token=adm)
        if not any(a['professionalId']==generator['professionalId'] and a['status']=='Active' for a in assignments):
            h.request('POST',h.API+'/api/v1/patients/'+pid+'/professionals',{'professionalId':generator['professionalId'],'relationshipType':'Assigned'},token=adm)
    if not value.get('aiScenario'):
        ai=value['accounts']['aiPatient']
        today=dt.datetime.now(__import__('zoneinfo').ZoneInfo('America/Bogota')).date()
        enrollment=api('POST','/api/v1/program/enrollments/me',ai,{'patientId':None,'templateId':None,'timezone':'America/Bogota','startLocalDate':str(today)})
        eid=enrollment['id'];week=2
        with h.connect() as db:
            db.execute('SET TRANSACTION READ ONLY')
            row=db.execute('SELECT to_jsonb(w) FROM app.program_weeks w WHERE enrollment_id=%s AND week_number=%s',(eid,week)).fetchone()[0]
            db.rollback()
        detail='/api/v1/program/enrollments/'+eid+'/week/2';suggestions='/api/v1/program/erp/enrollments/'+eid+'/weeks/2/ai-suggestions'
        value['aiScenario']={'enrollmentId':eid,'weekNumber':week,'weekSha256Before':digest(row),'weekDetailPath':detail,'suggestionsPath':suggestions,'manualSetupEnrollment':True,'suggestionApplied':False,'aiGenerated':False}
    state=read_state(value)
    assert state['patient']['completedTests']==8 and not state['patient']['enrollments']
    assert protected(value)==value['protectedBefore'],'Sentinelas ajenos modificados'
    value.update(stage='ready-8-of-9',preparedAt=dt.datetime.now(dt.timezone.utc).isoformat(),state=state)
    write(META,value);print(json.dumps({'stage':value['stage'],'metadata':str(META),'patientId':account['patientId'],'aiPatientId':value['accounts']['aiPatient']['patientId']}))

def complete_tests():
    value=load();account=value['accounts']['patient'];last=value['lastTest']
    state=read_state(value)
    if state['patient']['completedTests']==9:
        assert len(state['patient']['enrollments'])==1,'No reparar inscripciones desde helper'
    else:
        assert state['patient']['completedTests']==8 and not state['patient']['enrollments']
        path='/api/v1/health-tests/me/tests/'+last['assignmentId'];session=token(account)
        h.request('POST',h.API+path+'/start',token=session)
        result=h.request('POST',h.API+path+'/submit',last['submitPayload'],token=session)
        assert result['status']=='completed';value['finalEvaluationId']=result['id'];write(META,value)
    for attempt in range(15):
        state=read_state(value)
        if len(state['patient']['recommendations'])==1:break
        time.sleep(1)
    assert state['patient']['completedTests']==9 and len(state['patient']['enrollments'])==1 and len(state['patient']['recommendations'])==1
    enrollment=state['patient']['enrollments'][0]
    assert enrollment['status']=='PendingAssignment' and enrollment['current_week_number']==0
    proposal=state['patient']['recommendations'][0]
    assert proposal['nutritionPlan'] and proposal['exerciseRoutine'] and proposal['podcastSequence']
    assert protected(value)==value['protectedBefore'],'Sentinelas ajenos modificados'
    value.update(stage='completed-9-of-9-pending-assignment',completedAt=dt.datetime.now(dt.timezone.utc).isoformat(),state=state,
        contract={'immediateActive':False,'pendingAssignment':True,'automaticFallbackDays':7,'manualApplyPerformed':False})
    write(META,value);print(json.dumps({'stage':value['stage'],'tests':9,'enrollments':1,'recommendations':1,'nutritionPlan':bool(proposal['nutritionPlan']),'routine':bool(proposal['exerciseRoutine']),'podcastCells':len(proposal['podcastSequence'])}))

def generate_unapplied():
    value=load();scenario=value['aiScenario'];generator=value['accounts']['generator']
    before=read_state(value)
    assert before['unapplied']['weekEqualsBaseline'] and before['unapplied']['applicationAudits']==0
    current=api('GET',scenario['suggestionsPath'],generator)
    if current['status']=='empty':
        current=api('POST',scenario['suggestionsPath'],generator,{'language':'es'})
    elif current['status']=='ok' and not current['draft']['suggestions'] and scenario.get('contextLogSeeded') and not scenario.get('contextRegenerationAttempted'):
        scenario['previousEmptyBriefId']=current['draft']['id']
        scenario['contextRegenerationAttempted']=True;write(META,value)
        current=api('POST',scenario['suggestionsPath'],generator,{'language':'es'})
    assert current['status']=='ok' and current['draft']['suggestions'],'Sin sugerencias reales: no fabricar borrador'
    draft=current['draft']
    assert all(s['state']=='draft' and s['appliedAt'] is None and s['appliedBy'] is None for s in draft['suggestions'])
    for _ in range(2):
        reread=api('GET',scenario['suggestionsPath'],generator)
        assert reread['draft']['id']==draft['id']
        api('GET',scenario['weekDetailPath'],generator)
    after=read_state(value)
    assert after['unapplied']['weekEqualsBaseline'] and after['unapplied']['applicationAudits']==0
    assert protected(value)==value['protectedBefore'],'Sentinelas ajenos modificados'
    scenario.update(aiGenerated=True,suggestionApplied=False,briefId=draft['id'],suggestionCount=len(draft['suggestions']),draftState=draft['state'],
        startDate=draft['startDate'],endDate=draft['endDate'],qaWindowEndsAt=draft['startDate']+'T00:00:00-05:00',
        generationSource='Postman HTTP simulado + handler/guard/store reales' if draft.get('model')=='postman-weekly-synthetic' else 'Proveedor IA live',
        model=draft.get('model'))
    value['state']=after;write(META,value)
    print(json.dumps({'status':'ready-unapplied','suggestions':len(draft['suggestions']),'weekUnchanged':True,'applyAuditCount':0}))

def seed_ai_context():
    value=load();scenario=value['aiScenario']
    assert read_state(value)['unapplied']['applicationAudits']==0
    if not scenario.get('contextLogSeeded'):
        # Registro sintético del paciente QA nuevo: sin macros, fotos ni datos clínicos reales.
        request_id='qa411back-'+value['run']+'-context-des'
        result=api('POST','/api/v1/program/nutrition/log',value['accounts']['aiPatient'],{'mealCode':'des','localDate':None,'intake':None,'client_request_id':request_id})
        scenario.update(contextLogSeeded=True,contextLogRequestId=request_id,contextLogSource='API real; registro sintético sin macros ni foto, solo paciente QA propio')
        write(META,value)
    assert protected(value)==value['protectedBefore']
    print(json.dumps({'contextLogSeeded':True,'protectedUnchanged':True}))

def seed_simulated_suggestion():
    value=load();scenario=value['aiScenario'];generator=value['accounts']['generator']
    current=api('GET',scenario['suggestionsPath'],generator)
    if current.get('draft') and current['draft'].get('model')=='postman-weekly-synthetic' and current['draft'].get('suggestions'):
        generate_unapplied();return
    assert read_state(value)['unapplied']['applicationAudits']==0
    session=token(generator);me=h.request('GET',h.AUTH+'/api/auth/me',token=session)
    reader={'patientId':value['accounts']['aiPatient']['patientId'],'enrollmentId':scenario['enrollmentId'],'weekNumber':2,
            'accounts':{'generator':{**generator,'permissions':me['permissions']}}}
    write(EVIDENCE/'canonical-reader-input.json',reader)
    config=json.loads((ROOT/'coppAddresdBack/src/CoppAddresd.Api/appsettings.Development.json').read_text(encoding='utf-8-sig'))
    environment=dict(os.environ);environment['COP_TEST_DB_CONNECTION']=config['ConnectionStrings']['DefaultConnection']
    runtime=Path('C:/Users/Luis/AppData/Local/Temp/opencode/qa-f4-api/src/CoppAddresd.Api')
    command=['dotnet','run','--project',str(EVIDENCE/'suggestion-seed/Seed.csproj'),'-p:OutputPath=bin/Fixture411/','--',
        str(EVIDENCE/'canonical-reader-input.json'),str(EVIDENCE/'synthetic-suggestion-seed.json'),str(runtime/'appsettings.json'),str(runtime/'appsettings.Development.json')]
    with (EVIDENCE/'synthetic-suggestion-seed.log').open('w',encoding='utf-8') as log:
        result=subprocess.run(command,env=environment,stdout=log,stderr=subprocess.STDOUT,timeout=120)
    del environment,config
    assert result.returncode==0,'El proveedor Postman debe estar iniciado en 4533; revisar log sin mostrar secretos'
    generate_unapplied()

def check():
    value=load();state=read_state(value);now=dt.datetime.now(dt.timezone.utc).isoformat()
    same=protected(value)==value['protectedBefore']
    assert same,'Sentinelas ajenos modificados'
    if value.get('aiScenario',{}).get('aiGenerated'):
        assert state['unapplied']['weekEqualsBaseline'] and state['unapplied']['applicationAudits']==0,'La semana QA cambió o se aplicó una sugerencia; no resetear'
    result={'commit':value['commit'],'capturedAt':now,'stage':value['stage'],'state':state,'protectedUnchanged':same,'protectedAfter':protected(value)}
    EVIDENCE.mkdir(parents=True,exist_ok=True);write(EVIDENCE/'fixture-check.json',result)
    print(json.dumps({'stage':value['stage'],'completedTests':state['patient']['completedTests'],'protectedUnchanged':same,'unapplied':state.get('unapplied')},default=str))

if __name__=='__main__':
    parser=argparse.ArgumentParser(description=__doc__);parser.add_argument('mode',choices=['prepare','complete-tests','generate-unapplied','seed-ai-context','seed-simulated-suggestion','check']);args=parser.parse_args()
    try:
        {'prepare':prepare,'complete-tests':complete_tests,'generate-unapplied':generate_unapplied,'seed-ai-context':seed_ai_context,'seed-simulated-suggestion':seed_simulated_suggestion,'check':check}[args.mode]()
    except Exception as error:
        print('QA411BACK fallo: '+type(error).__name__+'; no se imprimen cuerpos/secretos. '+(str(error) if isinstance(error,(AssertionError,RuntimeError)) else ''),flush=True)
        raise SystemExit(1) from None
