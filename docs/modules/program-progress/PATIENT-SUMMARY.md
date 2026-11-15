# Resumen IA del Perfil 360 — 4.4b

Contrato interno: `ai-service` **041a602**, `docs/patient-summary.md` y
`app/schemas/patient_summary.py`. Es una estimación para revisión humana; permanece
`draft`, no cambia planes ni publica nada. Solo backend consume IA.

## Contrato ERP

Ruta: `/api/v1/program/erp/patients/{patientId}/ai-summary`.

| Operación | Petición | Capacidad |
| --- | --- | --- |
| GET | `?language=es` o `en`; por defecto es | Program.Profile360View + Program.AiView |
| POST | JSON `{ "language": "es" }`; `{}` usa es | las anteriores + Program.AiManage |

Audiencia ERP y alcance del paciente obligatorios. Admin exceptúa solo asignación;
director clínico solo sus pacientes; NutritionReviewer lee sin generar y
CareCoordinator no tiene acceso IA (matriz existente §10.1 y WellbeingRolePermissions).
Anónimo 401; audiencia app o capacidad ausente 403; ajeno 404; idioma inválido 400.
Ambas respuestas llevan Cache-Control: no-store.

HTTP 200: `{ "status": "ok|empty|insufficient|unavailable", "summary": objeto|null }`.
GET sin draft del idioma: empty. Fallo, timeout, circuito abierto, contrato inválido o
consentimiento no disponible: unavailable con summary null. Datos insuficientes:
insufficient con summary null. La UI omite ese bloque y conserva el resto del Perfil 360,
que no depende de esta petición.

En ok, summary contiene `id`, `kind=patient_summary`, `isEstimate=true`, `summary`
(texto), `sourceRefs`, `attention:[{text,sourceRefs}]`, `model` nullable, `generatedAt`
(UTC del servicio), `language`, `startDate`, `endDate` y
`sources:[{ref,section,from,to}]`. Modelo ausente queda null, sin inventarlo.
La UI etiqueta «estimado por IA» y muestra modelo/fecha disponibles. Trata texto como
texto plano y resuelve `[ref]` por sources hacia la sección/intervalo del paciente actual
mediante los accesos autorizados de la UI. Nunca ejecuta HTML ni URLs del modelo.

GET nunca genera ni llama a IA: lee el último draft del idioma por generated_at/id
descendentes. Un POST fallido no devuelve el draft anterior como si fuera nuevo y
tampoco lo borra; permanece consultable por GET con su fecha original. Sin generación
automática al abrir el resto del perfil. UI/4.7 y sugerencias 4.5 son tareas separadas.

## Hechos enviados y privacidad

Siete días inclusivos hasta hoy en patient_profiles.timezone, fijados por backend.
Snapshot REPEATABLE READ corto, cerrado antes de la red; consultas agregadas acotadas
por paciente/intervalo, sin SQL por registro ni gráficos completos de entidades.

| ref / section | Dato seleccionado |
| --- | --- |
| program.current / program | estado, semana e inicio registrados de la última inscripción ya iniciada; contexto histórico explícito |
| nutrition.records / nutrition | registros de comida y días con registros, excluyendo agua; no equivale a cumplimiento del plan |
| exercise.completions / exercise | tareas de ejercicio registradas como completadas en el intervalo |
| podcasts.completions / podcasts | tareas de podcast registradas como completadas en el intervalo |
| tests.completed / tests | evaluaciones completadas en el intervalo; sin respuestas, scores ni diagnósticos |
| measurements.recorded / measurements | mediciones observadas en el intervalo y registradas antes de captura; sin valores/notas/inferencia clínica |

Solo hechos con datos conocidos; cero no se envía como prueba de incumplimiento.
Sin hechos, no llama a IA ni persiste. No inventa alertas, evolución, metas ni tratamientos.
No envía nombre, documento, contacto, fotos, claves, URLs, IDs, notas libres, expediente
completo ni zona horaria. Es una selección parcial, no un resumen exhaustivo del expediente.
Se evaluó CQRS: no hay KPI/dashboard nuevo, no corresponde otra pre-agregación.

## Seguridad, transporte y almacenamiento

ILegalConsentService comprueba patient-ai antes de leer/generar y de leer un draft,
según la política configurada. Sujeto solo en opciones internas HTTP, nunca JSON/hash;
RequireRecipientConsent=true evita la excepción ERP del AiConsentHttpHandler en la
frontera externa. Antes de guardar se revalidan permiso, asignación y consentimiento.
Revocación durante la red no escribe. CancellationToken llega a queries, HTTP, cuerpo
y SaveChanges; cancelar no genera una fila ni devuelve una estimación.

Reutiliza AiService, X-Internal-Key, correlación y ProgramNarrativeResilience compartida:
3 reintentos, circuito tras 5 fallos/30s; timeout total configurable acotado a 1..25 s,
incluido el cuerpo (máximo 64 KiB). JSON estricto, tipo/estimación/UTC/límites y citas
comprobadas contra facts. Rechaza referencias inventadas, URLs y HTML. Sin cuerpo ni
excepción interna del proveedor en la respuesta. Esto no certifica cada frase: exige
revisión humana.

Cada generación válida añade app.patient_ai_briefs con kind patient_summary, state draft,
respuesta/idioma/período/descriptores en draft_json, modelo y fecha en columnas propias
y SHA-256 de los bytes UTF-8 exactos enviados. No duplica entradas clínicas; el hash
no reconstruye el request ni es idempotencia. Concurrencia conserva ambas generaciones.
GET filtra idioma en jsonb usando el índice de paciente/tipo/fecha/id; no hay índice
redundante por idioma/hash/estado. Narrativa y ai_draft_json nutricional conservados.

Migración compatible **20261115184418_ExpandPatientAiBriefModelName**: solo amplía
model_name de varchar(120) a varchar(200), máximo de 4.4a; lock_timeout=5s.
Down comprueba bajo lock que no haya nombres mayores de 120 y falla antes de truncarlos.
Sin backfill, flags ni tabla nueva. No aplicada a BD compartida: la dispone el orquestador.

## Evidencia local (15-11-2026)

- Build 0 errores; unitarios backend **2692 aprobados/105 omitidos**.
- PostgreSQL aislado **25/25**: cuatro nuevos de resumen, tres de almacenamiento 4.3
  y regresiones de asignación/narrativa, cierre y ciclo IA. Fuentes propias, privacidad,
  hash, modelo200, idioma/historial, revocación durante red, fallo/timeout/insuficiencia/
  cancelación sin escritura ni borrado previo; Down/Up y modelo EF coherente.
- HTTP del controller: 401/403/404, binding ES/EN, unavailable sin contenido y no-store;
  matriz por rol e inventario de guards.
- Request backend capturado validado por Pydantic del commit exacto **041a602**:
  cuatro campos exactos, tres hechos, idioma en, siete días.
- Simulación Postman con cliente .NET real: tres requests registrados, 200 ok,
  200 insufficient, 503; aserciones aprobadas. No se llamó a un LLM real ni se modificó
  o reinició el stack compartido. No se afirma QA visual de 4.7 ni integración live.

```powershell
dotnet build --no-restore -p:OutputPath=bin/Validation44b/ -v quiet
dotnet test tests/CoppAddresd.UnitTests --no-build -p:OutputPath=bin/Validation44b/ -v quiet
dotnet test tests/CoppAddresd.IntegrationTests --no-build -p:OutputPath=bin/Validation44b/ --filter 'FullyQualifiedName~PatientSummary_|FullyQualifiedName~PatientAiBrief_|FullyQualifiedName~ProgramAssignmentIntegrationTests|FullyQualifiedName~CicloIA_|FullyQualifiedName~ProgramaCierre_' -v normal
```

Simulación propia, iniciar en background y cerrar al acabar:

```powershell
postman simulation run postman/simulations/patient-summary.sim.yaml --port 4524 --output ndjson --no-history
$env:COP_PATIENT_SUMMARY_SIM_URL='http://127.0.0.1:4524'
dotnet test tests/CoppAddresd.UnitTests --no-build -p:OutputPath=bin/Validation44b/ --filter 'FullyQualifiedName~Cliente_ContratoCompleto_BytesConsentimiento_InsuficienciaY503_ConSimulacionOpcional' -v quiet
```

Mock construido desde 041a602. Sin búsqueda en workspace del propietario: el CLI
instalado responde `unknown command 'status'`; no se inicializó/publicó workspace.
Fuente y alcance de evidencia en `postman/simulations/patient-summary.context.yaml`.
