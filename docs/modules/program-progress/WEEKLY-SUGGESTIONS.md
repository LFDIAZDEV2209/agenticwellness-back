# Sugerencias del plan semanal — 4.5b

Contrato interno **ai-service 612fdfe** (`docs/weekly-plan-suggestions.md` y
`app/schemas/weekly_plan_suggestions.py`). El profesional genera a pedido, revisa
y confirma cada alternativa. Abrir, listar y generar no aplican cambios.

## API ERP

Base: `/api/v1/program/erp/enrollments/{enrollmentId}/weeks/{weekNumber}/ai-suggestions`.

| Operación | Entrada | Capacidades |
| --- | --- | --- |
| GET base | `?language=es` o `en`, predeterminado es | Program.Profile360View + Program.AiView |
| POST base | `{ "language": "es" }`; `{}` usa es | las anteriores + Program.AiManage + Program.Edit |
| POST base `/{briefId}/suggestions/{index}/apply` | `{ "confirmed": true }` | las mismas de generación |

Audiencia ERP, paciente asignado y política de consentimiento `patient-ai` en todas
las operaciones. Admin exceptúa solo asignación. Matriz §10.1 existente:
ClinicalDirector puede generar/aplicar a propios; NutritionReviewer solo leer;
CareCoordinator no dispone de IA. Anónimo 401, audiencia/capacidad ausente 403,
inscripción o borrador ajeno 404. El actor se obtiene de la identidad autenticada.
Todas las respuestas son `no-store`.

GET/POST generación: HTTP 200 `{ "status": "ok|empty|insufficient|unavailable", "draft": objeto|null }`.
GET devuelve el último borrador de esa inscripción, semana e idioma, incluyendo
aplicaciones parciales y completas. GET nunca llama a IA. Error de proveedor,
timeout, circuito, contrato inválido o consentimiento no disponible: unavailable,
draft null; la UI omite el bloque y mantiene su editor semanal manual. Una
generación fallida conserva el borrador anterior sin devolverlo como recién generado.

El draft contiene `id`, `kind=weekly_plan_suggestions`, `isEstimate=true`, `state`,
`model` nullable, `generatedAt` UTC, `language`, intervalo objetivo, `planVersion`,
`stale`, `sources:[{ref,section,from,to}]` y `suggestions`:

```json
{
  "index": 0,
  "date": "2026-11-16",
  "slotKind": "routine",
  "kind": "routine",
  "resourceId": "00000000-0000-0000-0000-000000000001",
  "label": "Alternativa del programa",
  "reason": "Alternativa para revisión profesional [program.current].",
  "sourceRefs": ["program.current"],
  "state": "draft",
  "appliedAt": null,
  "appliedBy": null
}
```

El índice es estable dentro del borrador y empieza en cero. kind es routine,
podcast o rest (resourceId null en descanso). La UI presenta «estimado por IA»,
modelo/fecha disponibles, fuentes locales del paciente e intervalo; trata label y
reason como texto plano. Exige una acción humana por sugerencia, sin aplicar todo
al cargar ni enviar IDs de recursos elegidos por el cliente.

Aplicar devuelve HTTP 200 `{ "status": "applied|already_applied", "briefId": uuid,
"index": número, "week": EnrollmentWeekDetailDto }`. Falta de confirmación explícita:
422 HUMAN_CONFIRMATION_REQUIRED. Descanso deshabilitado: 422 REST_DAYS_DISABLED.
Semana iniciada/con historia, inscripción inactiva, plan cambiado o alternativa
retirada/modificada: 409; recargar o volver a generar. Índice fuera de 0..4: 400;
un índice válido sin sugerencia o un borrador de otra inscripción/semana: 404.
Cada acción vuelve a comprobar
alcance, capacidades, consentimiento, fecha, contenido efectivo y recurso.

## Selección y contrato AI

Semana futura editable de una inscripción activa, con máximo siete días y corte
en el día 83. Catorce slots como máximo: rutina y podcast por día. Los hechos
desidentificados reutilizan [el resumen 4.4b](PATIENT-SUMMARY.md), con fechas
explícitas de los siete días locales recientes; no se hacen pasar por resultados
de la semana futura. Sin hechos u opciones se responde insufficient sin llamar a IA.

BACK limita alternativas de rutina a las ya incluidas en esa plantilla, activas,
sin contraindicaciones declaradas y dificultad igual o menor que la actual; exige
rutina actual conocida y válida. Cualquier restricción de seguridad vigente
suprime alternativas de rutina/descanso. Descanso además exige el interruptor
Program:RestDays:Enabled. Podcasts publicados, con audio válido, sin demos;
máximo diez candidatos y se excluye siempre el contenido actual. Es una selección
conservadora, no una certificación clínica de adecuación; decide el profesional.

JSON interno exacto: startDate, endDate, language, facts y slots. Solo referencias
opacas por slot/opción, etiquetas de catálogo y hechos seleccionados; ningún ID
de paciente, inscripción o recurso, nombre del paciente, contacto, foto, URL,
storageKey ni expediente completo. El sujeto de consentimiento viaja en opciones
HTTP internas con RequireRecipientConsent=true, fuera del JSON/hash.

POST interno `/internal/program/weekly-plan-suggestions`, X-Internal-Key, política
de resiliencia y correlación existente. Timeout total 1..25 s, cuerpo máximo
64 KiB, JSON estricto. Se verifican kind/estimación/UTC/modelo ≤120, máximo cinco
sugerencias, una por slot, opciones pertenecientes a ese slot, fuentes e inline
refs existentes; se rechazan HTML/URLs/referencias inventadas. ok sin sugerencias
es válido; insufficient lleva lista vacía y modelo null. No se exponen detalles
del proveedor ni se registra el texto clínico en logs. Cancelación propagada.

## Persistencia, personalización y bitácora

Reutiliza `app.patient_ai_briefs` 4.3, kind weekly_plan_suggestions; sin migración
nueva. Guarda modelo/fecha del servicio, SHA-256 de los bytes exactos enviados y
JSON mínimo con el mapeo backend de cada elección, razones/fuentes y versión del
plan. No duplica el request clínico. Lectura acotada al último registro por
paciente/kind/fecha/id usando el índice existente; filtro JSON por inscripción,
semana e idioma. Sin KPI ni agregación nueva: no aplica pipeline CQRS adicional.

La confirmación reutiliza ProgramRepository.ReplaceEnrollmentWeekTasksAsync y el
editor semanal existente. Bajo los locks de inscripción/semana y borrador,
revalida versión y alternativa (incluida versión del recurso), fija plantilla y
recurso y modifica solo la celda elegida: origin=individual, editedBy/editedAt.
Conserva puntos, orden, las demás celdas, historial y asignaciones legacy.

La misma transacción inserta el evento ProgramWeekTasksChanged con old_data,
new_data y operaciones, más `metadata.ai_application` (brief_id, suggestion_index,
confirmed, plan_version, input_hash), y marca appliedAt/appliedBy en el borrador.
La bitácora semanal existente muestra el evento sin un listado paralelo. Un fallo
en cualquiera de esas escrituras revierte todo; no se mantiene transacción durante
la llamada a IA. Las comprobaciones dentro del editor solo acceden a la BD local.

El borrador queda draft mientras tenga sugerencias pendientes y applied cuando
todas están confirmadas. Tras cada aplicación se actualiza su versión esperada,
conservando la versión original de generación: permite continuar ese mismo
borrador, pero una edición manual u otro borrador lo deja stale. Confirmaciones
simultáneas o reintentos de un índice ya aplicado devuelven already_applied con
la semana actual, sin reescribir ni duplicar bitácora; nunca restauran una celda
que otro profesional haya cambiado después.

## Verificación reproducible

Build final: **0 errores**. Unitarios backend: **2758 aprobados / 105 omitidos**.
PostgreSQL aislado: **8/8 casos nuevos** de generación/lectura sin mutación,
aplicación parcial, metadatos/bitácora, confirmación obligatoria, concurrencia y
replay, recursos retirados, edición manual/versión, alcance revocado tras red,
semana iniciada, seguridad/descanso, 503/JSON inválido/timeout/insuficiencia/
consentimiento y cancelación; rollback tanto por fallo al marcar el borrador como
por cancelación después de snapshot/bitácora. Los unitarios cubren además matriz
§10.1, inventario de guards, audiencia HTTP, binding, no-store y referencias
desconocidas/cross-slot, HTML/URLs, límite de cuerpo y errores sin detalles.

Regresión ampliada del editor, ciclos/nutrición, resumen/almacenamiento y
asignación: **148/148** aprobadas. Los ocho específicos se ejecutaron también
contra el build final e incorporan un caso adicional de cancelación: **149 pruebas
PostgreSQL distintas** aprobadas entre ambas ejecuciones, sin omitidos.

```powershell
dotnet build --no-restore -p:OutputPath=bin/Validation45b/ -v quiet
dotnet test tests/CoppAddresd.UnitTests --no-build -p:OutputPath=bin/Validation45b/ -v quiet
# COP_TEST_DB_CONNECTION local: el fixture crea y elimina BD copp_nutrition_test_*.
dotnet test tests/CoppAddresd.IntegrationTests --no-build -p:OutputPath=bin/Validation45b/ --filter 'FullyQualifiedName~NutritionPlanWorkflowTests|FullyQualifiedName~ProgramAssignmentIntegrationTests' -v quiet
```

La simulación Postman usa solo datos sintéticos y un proceso propio:

```powershell
postman simulation run postman/simulations/weekly-suggestions.sim.yaml --port 4525 --output ndjson --no-history
$env:COP_WEEKLY_SUGGESTIONS_SIM_URL='http://127.0.0.1:4525'
dotnet test tests/CoppAddresd.UnitTests --no-build -p:OutputPath=bin/Validation45b/ --filter FullyQualifiedName~WeeklyCliente_BytesConsentimiento -v quiet
```

Cliente .NET real contra simulación: 200 ok, 200 insufficient y 503, tres requests
y aserciones aprobadas. Request HTTP del fixture PostgreSQL validado con Pydantic
del commit exacto 612fdfe: cinco campos, un hecho, catorce slots, siete días, es.
Fuente/alcance en `postman/simulations/weekly-suggestions.context.yaml`. El CLI
Postman 1.76.0 no implementa `status`; no se inicializó ni publicó workspace.
No se afirma integración con un LLM live ni QA visual. Stack compartido intacto;
la simulación propia se cerró al terminar.
