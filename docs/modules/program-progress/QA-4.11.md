# Soporte backend del recorrido 4.11

Preparación del 15-11-2026, **e0a9c29**, sin cambios de producto. El recorrido UI
4.11 sigue pospuesto por el rediseño; esta preparación no acredita su cierre.
No consumir ni resetear la fixture APP anterior `4.11-app-fixture.json`.

Metadata propia, sin contraseñas ni tokens:
`.ai/operations/organizar-experiencia-bienestar/4.11-back-fixture.json`.
Helper operativo: `4.11-back-fixture-helper.py` en ese mismo directorio compartido.
Copia versionada: [prepare-back-4.11.py](../../../scripts/qa/program-assignment/prepare-back-4.11.py).
[Evidencia versionada y manifiesto](evidence/4.11/README.md); origen compartido:
`openspec/changes/organizar-experiencia-bienestar/revision-4.11-back/`.

## Estados preparados y límites del contrato

| Caso | Datos nuevos de QA | Estado real observado |
| --- | --- | --- |
| Terminar los tests | Paciente `8ce7fbef-8533-4100-8dd3-39f193ee4617` | Nueve tests completados por start/submit reales, una inscripción y una propuesta únicas |
| Nutrición post-tests | Prescripción `b76e7a82-875e-4abd-91f9-f03c2c5e2a95` | Publicada por el worker, 30 días, lectura APP 200; no se simuló aprobación clínica |
| Rutina y podcasts de la propuesta | Recomendación `ffa0478b-7387-47fd-a624-59107b6fce0a` | Plan Obesidad — Déficit calórico, Caminata Activa, secuencia de siete celdas genéricas de podcast |
| Programa post-tests | Inscripción `f897e629-b546-4eae-9629-d3c79c90af1a` | **PendingAssignment**, semana 0; no equivale a contenido activo inmediato |
| Sugerencia sin aplicar | Paciente independiente `5e3a337e-b29a-4c10-a8a8-904e55bd09e1`, inscripción `aa946028-0b77-4c73-a412-5c00e5936737`, semana 2 | Cinco sugerencias draft consultables por GET; ninguna aplicada; semana intacta y cero eventos ai_application |

La nutrición tiene un workflow independiente del programa: su publicación
automática tras 9/9 no activa la inscripción ni aplica la rutina propuesta.
El programa conserva la espera/revisión y el fallback automático de **siete días**
desde recommendation.created_at, condicionado por Program:Assignment:Enabled.
No se cambió ese flag ni se adelantaron fechas. La API ERP de aplicar la antigua
propuesta de asignación fue retirada; no inventar una ruta sustituta.

La propuesta conserva la secuencia de la plantilla, sin rellenar audio. El
inventario del paciente post-tests tiene **7 referencias Published, 0 claves
vacías y 5 claves demo conocidas**. La reproducción completa de esa secuencia
no está acreditada. **1.14 es la reparación reversible de huecos según §5.1,
solo sobre items program; no es una tarea de carga de mp3.** Dos referencias no demo no prueban que sus blobs
existan físicamente; no se realizó sonda S3. No se cambió el catálogo, la plantilla
global ni referencias del paciente para ocultar esta limitación.

El paciente de IA tiene una inscripción **manual de setup** separada del caso
post-tests. No demuestra alta automática. Semana objetivo **22–28/11/2026**,
conservada sin activar/completar; revalidar antes del **22/11 00:00 Bogotá**.
El helper no resetea una fixture consumida ni mueve fechas para prolongarla.

## Contratos para APP y ERP

- `GET /api/v1/health-tests/me/assignments?scope=initial`: nueve required,
  completed; identidad del paciente por JWT APP. El setup ejecutó start/submit
  con clientRequestId estable, sin copiar evaluaciones, resultados o scores.
- `GET /api/v1/me/nutrition-plan`: plan real publicado de 30 días, sin depender
  del bloque de resumen/sugerencias IA del ERP.
- `GET /api/v1/program/me/snapshot`: 404 NO_ACTIVE_ENROLLMENT para el principal
  PendingAssignment; 200 y tareas reales para el compañero Active. La UI debe
  representar esa diferencia, no inventar misiones activas para el principal.
- `GET /api/v1/program/me/completion`: completion=null para el principal; no
  confundir completar los tests con terminar el programa de 83 días.
- Resumen: `/api/v1/program/erp/patients/{patientId}/ai-summary?language=es|en`.
  GET nunca genera; 200 `{status:"empty",summary:null}` sin borrador.
  POST de generación ante proveedor caído: 200
  `{status:"unavailable",summary:null}`. Lectura/generación requieren ERP,
  alcance, Profile360View/AiView; generar añade AiManage y consentimiento.
- Sugerencias:
  `/api/v1/program/erp/enrollments/{id}/weeks/2/ai-suggestions`.
  GET nunca genera/aplica. POST genera un draft; ante proveedor caído: 200
  `{status:"unavailable",draft:null}`. Generar/aplicar añade AiManage y
  Program.Edit a los permisos de lectura y exige consentimiento.
- Detalle de semana: `/api/v1/program/enrollments/{id}/week/2`, una base distinta
  de las sugerencias ERP. Confirmar una sugerencia exige otra operación explícita
  `POST .../ai-suggestions/{briefId}/suggestions/{index}/apply` con confirmed=true.
  **No llamar a esa operación en este guion sin acción.**

Resumen y sugerencias son estimaciones; modelo nullable, fecha UTC y fuentes.
Los fallos no borran el draft previo, no lo devuelven como recién generado ni
modifican plan/semana. Lecturas y generación llevan no-store. Cancelar la llamada
propaga cancelación y no publica un borrador parcial. Ver los contratos completos
en [PATIENT-SUMMARY.md](PATIENT-SUMMARY.md) y
[WEEKLY-SUGGESTIONS.md](WEEKLY-SUGGESTIONS.md).

## Procedencia del borrador sin aplicar

Dos solicitudes al proveedor live devolvieron **ok con cero sugerencias**; es un
resultado permitido y no demuestra una alternativa pendiente. Se conservan sus
dos briefs sin borrarlos. Una captura canónica real comprobó 14 slots, 22 opciones
de rutina y 69 de podcast; no se inventaron opciones ni fuentes.

Para dejar el caso determinista se invocó **GenerateWeeklySuggestionsHandler,
PatientScopeGuard, WeeklySuggestionsStore y EF reales** contra el proveedor HTTP
Postman existente de ai-service, únicamente para el compañero nuevo. Modelo
**postman-weekly-synthetic**, sin presentarlo como respuesta LLM live. El handler
validó hechos/opciones y guardó un tercer brief; no hubo INSERT manual de un
borrador, POST apply ni cambios al contenido. GET con JWT QA consulta las cinco
alternativas. La semana mantiene su SHA-256 y todas conservan state=draft,
appliedAt/appliedBy=null. La metadata señala expresamente generationSource.

El setup añadió al compañero un registro de desayuno sintético por API, sin
macros ni foto, con clave idempotente; no pertenece a `0c487f75` ni a R7.
Sus tres briefs y este registro son escrituras de preparación autorizadas, no
evidencia de cero escrituras globales.

## Reproducción segura

```powershell
$ops = 'C:/Users/Luis/Documents/CoppAddresd/Repos/.ai/operations/organizar-experiencia-bienestar'
# Solo lectura; comando habitual para quien consume la fixture ya preparada.
python "$ops/4.11-back-fixture-helper.py" check
python C:/Users/Luis/Documents/CoppAddresd/Repos/openspec/changes/organizar-experiencia-bienestar/revision-4.11-back/probe-readonly.py
```

`prepare` solo admite preparación incompleta/8-de-9; rechaza resetear el principal
ya consumido. `complete-tests` completa únicamente el paciente propio y verifica
una inscripción/propuesta, sin aplicar. `generate-unapplied` consulta/genera, pero
no aplica; exige al menos una sugerencia y comprueba baseline/auditoría.
`seed-simulated-suggestion` conserva el brief sintético existente; para crearlo
requiere el proveedor Postman propio en 4533. El runner C# está restringido al
UUID del compañero de esta ejecución. No cambiar ese guard para reutilizar datos
ajenos. El helper importa solo selección de respuestas del preparador APP; nunca
ejecuta su prepare/prepare-ai ni escribe en la fixture APP anterior.

Credenciales de los dos pacientes: Windows Credential Manager Generic/CurrentUser,
targets `CoppAddresd/QA/4.11-back/<run>/patient|aiPatient` en metadata. El generador
ERP reutiliza su target QA existente; no se ampliaron sus permisos en esta tarea.
No poner contraseñas, cookies, tokens o URLs firmadas en JSON, logs, HAR o capturas.

## IA caída y alcance de la evidencia

Se reutilizaron simulaciones existentes, sin modificar el stack compartido:
`patient-summary.sim.yaml` y `weekly-suggestions.sim.yaml`, puertos propios
4532/4533. Los **dos clientes .NET** ejecutaron cada uno 200 ok, 200 insufficient
y 503 contra Postman. El log confirma que alcanzaron el proveedor simulado.

**3/3 PostgreSQL aisladas, sin omitidas**, código e0a9c29:

1. NueveTests_EventoPostCommit_InscripcionPendienteYPropuestaAtomicas_Idempotentes:
   submit final/replay sin duplicación; fallo de narrativa conserva propuesta y
   snapshot, queda pending-narrative.
2. PatientSummary_IAFallida_InsuficienteTimeoutYCancelacion_NoPersistenNiBorranBorradorPrevio:
   HTTP 503, JSON inválido, insuficiencia, timeout y cancelación sin borrar el draft.
3. WeeklyAI_FallosIAInsuficienciaCancelacionYConsentimiento_NoPersistenNiAplican:
   HTTP 503, JSON inválido, referencias inválidas, timeout, cancelación e
   insuficiencia sin persistir/aplicar; consentimiento bloquea antes de llamar.

Son handlers/stores/clientes reales con proveedor controlado y bases nuevas
aisladas, no una caída real del AI8000 compartido. Las pruebas de clientes cubren
HTTP Postman; las de PostgreSQL cubren la degradación de aplicación y persistencia.
Los controllers proyectan esos resultados a los envelopes 200 documentados.
Lecturas reales separadas del runtime: **9 requests / 19 aserciones**, plan de
30 días, programa pendiente/activo y GET de cinco sugerencias sin aplicar.

Para la futura UI, interceptar solo los bloques IA del paciente QA en el navegador
propio y devolver los envelopes unavailable anteriores; no interceptar tests,
programa, recursos o nutrición. Comprobar que se omite la ayuda y permanecen
Perfil 360/editor manual/APP navegables. Retirar interceptores y repetir lectura
sana. Eso valida UI; no llamarlo prueba de proveedor caído atravesando backend.
No se ejecutaron pantallas en esta preparación: ese recorrido sigue pendiente.

Los checksums confirman intactos `0c487f75`, su comida/cierre rev7, 33 R7, los
**siete planes y cuatro cierres publicados existentes al corte inicial**, y los
datos/archivo de la fixture APP aparcada. Durante el trabajo apareció un octavo
plan publicado ajeno: la comparación global lo detectó y se conservó el fallo
de preflight. Al excluir exactamente esa fila añadida se recupera el SHA-256
inicial de las siete filas originales, sin cambiar el hash baseline. Se fijaron
sus IDs para que altas concurrentes posteriores no se confundan con cambios a
filas cerradas; el hallazgo y la prueba están en sentinel-diff.json y
sentinel-reconciliation.json. No se atribuye autor a esa alta sin más evidencia.
Ningún dato cerrado original se modificó. Sin nuevas
migraciones, push ni tasks.md. Procesos Postman propios cerrados tras verificar
PID/ruta/comando/descendencia; stack compartido intacto. Build de producto
e0a9c29 sigue cubierto por la evidencia 4.12; el runner QA C# compila y pasa aparte.
