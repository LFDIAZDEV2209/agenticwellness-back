# Rutinas por día — Fase 2

## Vista previa de la masiva de podcasts — 1.15b

Para la confirmación real de 1.24 usar
`POST /api/v1/program/content/bulk-assign-patients/preview` con el mismo cuerpo
`{patientIds,mediaId,semanaDesde,semanaHasta}`. La ruta fuerza simulación incluso
si se recibe `simulate:false`; una API anterior responde 404 a esta ruta.
El POST de aplicación también admite `simulate:true` opcional, apagado por defecto.

La simulación devuelve exactamente el resultado de 1.15, incluyendo
`aplicadas`, `omitidasSemanaIniciada` y `omitidasSinAlcance`, calculado por el mismo
planificador, validación y alcance. No reserva semanas: al confirmar se reevalúa
el estado vigente. Program.Edit sigue siendo obligatorio y los IDs ajenos solo
se devuelven si venían en la solicitud, sin datos clínicos ni nombres.

Usa Repeatable Read y `SET TRANSACTION READ ONLY` como protección de BD; retorna
antes de adjuntar entidades o Save. No cambia snapshots, timestamps, auditoría
ni asignaciones antiguas. El modo normal conserva la transacción Serializable.

## Asignación masiva de rutina — 2.16

`POST /api/v1/program/content/bulk-assign-patient-routines`
recibe `{patientIds,routineId,semanaDesde,semanaHasta}` y devuelve el mismo resultado
de 1.15: `{aplicadas,omitidasSemanaIniciada,omitidasSinAlcance}`. Program.Edit y
alcance por paciente, como la masiva de podcasts; Admin exceptúa alcance, nunca
permiso. Hasta 100 pacientes; rutina activa y rangos compatibles con inscripciones
activas. No exige que la plantilla ya usada por un paciente siga activa.

Ambas masivas comparten planificador y escritura serializable por conjuntos.
Solo se cambia ejercicio, como `individual`, con actor/fecha; una asignación
explícita de rutina retira el descanso de esas celdas. Preserva podcasts, nutrición
y sus metadatos. Omite semanas iniciadas por calendario derivado de la inscripción
o estado/activación/finalización; no usa fechas denormalizadas para decidirlo.
Las omitidas por alcance solo contienen IDs enviados por el solicitante.

Los conteos son pacientes distintos, no semanas, y aplicadas/omitidas por inicio
pueden solaparse en rangos mixtos. Repetir no reescribe snapshots ni timestamps.
La semana final tiene seis celdas y termina en el día 83. No crea asignaciones
antiguas. Cancelación, validación o conflicto concurrente revierte el lote entero.

## Bitácora del plan — 2.12

Cada PUT de tareas semanales confirmado añade un evento `ProgramWeekTasksChanged`
a `audit.activity_logs` con actor del servidor, fecha, antes/después y operaciones
asignar/descanso/restablecer. Reutiliza la tabla y el escritor semántico existentes.
Conserva la acción `Update` conocida por lectores anteriores; identifica el evento
en metadata, sin añadir valores de enum incompatibles durante el despliegue.
La fila y el snapshot se confirman juntos; si falla la auditoría se revierte la
edición y se descarta del tracking la semana fallida, para no reutilizarla después.

`GET /api/v1/program/enrollments/{id}/week/{weekNumber}/changes?page=1&pageSize=20`
requiere `Program.Profile360View` y alcance de la inscripción, también en el handler.
Devuelve `items/total/page/pageSize/totalPages`, máximo 100, orden fecha/ID descendente.
Cada entrada contiene `actorId`, `occurredAt`, `before`, `after` y `metadata`.
Antes/después envuelven `tasks_snapshot`; metadata declara inscripción, semana y
celdas/operaciones. Ajeno → 404 antes de leer/contar; Admin exceptúa asignación,
nunca permiso. El log administrativo global conserva su DTO sin detalle clínico.

## Detalle de la sesión completada — 2.11

`task_completions.detail_snapshot` es jsonb nullable, sin DEFAULT ni relleno masivo.
El servidor lo captura solo al completar `ejercicio`, dentro de la transacción
de completación/XP; ningún campo del cliente puede aportar ese detalle. Un replay
conserva la captura original, aunque cambie el catálogo después.

```json
{"routineId":"UUID","name":"Sesión original","exercises":[{"name":"Sentadilla","sets":3,"reps":12,"mediaRef":"UUID"}],"capturedAt":"2026-11-14T14:00:00+00:00"}
```

También conserva descripción, duración, descanso, músculo, equipo, indicaciones,
orden, peso, tempo y esfuerzo. `mediaRef` es un ID estable, nunca una URL firmada.
El detalle semanal y el snapshot diario de una sesión completada leen el nombre
y la prescripción de esa captura y la devuelven como `detailSnapshot` opcional.
Las URLs de demostración se resuelven por conjuntos solo para medios todavía
publicados; archivar un medio no lo reactiva desde el historial. Sesión sin rutina
conocida conserva esa ausencia. El legado con columna null conserva la lectura
anterior: no se inventan prescripciones históricas que no fueron capturadas.

Migración `20261114141434_AddExerciseSessionDetailSnapshot`: aditiva, compatible
con lectores anteriores, lock timeout 5 s. Down de mantenimiento rechaza retirar
la columna si tiene sesiones; no puede perder capturas silenciosamente. Up/Down/Up
se prueba con 10001 completaciones, y la edición posterior de rutina/ejercicios,
replay y retiro de demostración se verifican contra PostgreSQL real.

## Editor semanal anterior — 2.9

`SetWeekContentHandler` delega la escritura a `ReplaceEnrollmentWeekTasks`.
Asigna plan/rutina en las celdas de los días reales de esa semana como individual,
conservando podcasts y otras clases. Un ID null restablece esa dimensión al
programa; no elimina su misión ni su respaldo. Respeta semana iniciada y alcance
antes de leer la inscripción. No crea, completa ni recorta ninguna fila de
`routine_assignments` o `nutrition_plan_assignments`: este flujo las conserva
solo como lectura de respaldo. El rango antiguo reutiliza la misma operación
por semana. La semana 12 termina en el día 83.

## Edición semanal — 2.8

El PUT existente `/api/v1/program/enrollments/{id}/week/{weekNumber}/tasks`
acepta una lista parcial por `(weekday,taskCode)`: `routineId` asigna la rutina;
`rest:true` indica descanso; `reset:true` sin referencias restablece esa celda
a la plantilla semanal vigente (antes que la genérica). Las dos primeras son
`individual`; restablecer devuelve `program` (§5). Guarda actor y fecha.
No elimina días ni clases omitidos; un campo clínico null conserva el vínculo.

El inicio se calcula desde `start_local_date + (weekNumber-1)*7`, nunca desde
las fechas denormalizadas de la semana. Hoy o futuro es editable mientras no
haya activación, finalización ni historial; días anteriores quedan bloqueados.
El hoy se obtiene de la zona del paciente con TimeProvider (System por defecto).
Semana iniciada o pasada → 409 `PROGRAM_WEEK_STARTED`, sin cambiar historial,
XP ni snapshot. Respeta zona del paciente y los 83 días. Inscripción ajena → 404
con Program.Edit y paciente asignado, también dentro del handler; Admin exceptúa
asignación, nunca permiso. Rutina debe existir y estar activa. Los campos nuevos
son opcionales; el cuerpo del editor anterior sigue siendo válido.

La regresión 2.8-fix cubre fechas denormalizadas desfasadas en ambos sentidos,
ayer/hoy/mañana en zona Bogotá y conservación de la preedición al activar por
fecha y por semana perfecta. Los escenarios históricos fijan su reloj antes de
la preedición, para que el resultado no cambie según el día de ejecución del CI.
Se exige la suite completa `NutritionPlanWorkflowTests` antes del handoff.

## Interruptor de descanso — 2.7

`Program:RestDays:Enabled` (entorno `Program__RestDays__Enabled`) queda apagado
si falta, vale false o es inválido. Solo true permite `rest:true` en el PUT de
tareas semanales del paciente; apagado responde 422 `REST_DAYS_DISABLED` antes
de escribir. El campo opcional solo admite ejercicio sin routineId y no se
admite en el catálogo de plantillas. La lectura de descansos existentes sigue
siendo tolerante. No se habilita automáticamente tras 2.6: la app anterior tuvo
un error de consola `No queryFn`, aunque no mostró un error visible.

Rama `org/f2-coppAddresdBack`, base Fase 1 `185364e`.

## Resolución — 2.2

`EffectiveRoutineResolver` aplica ARQUITECTURA §7.1: descanso explícito termina la
cadena y devuelve rutina nula; luego rutina válida del snapshot; plantilla semanal
antes que genérica; asignación antigua activa y vigente del paciente. Sin fuente
válida hay hueco. Un snapshot sin `origin`, `rest` o `routine_id` conserva las
lecturas antiguas y cae por la misma cadena. Descanso gana incluso si un JSON
antiguo contradictorio trae además una rutina.

`EffectiveDayContentResolver` usa esa única política; `ProgramContentResolver`
delega en él para inscritos, tanto en lectura simple como en rango, sin volver a
seleccionar respaldos. El puente no expone rutina en un día de descanso.
No se crean asignaciones antiguas ni consultas dentro del calendario. Pacientes
sin inscripción conservan el contrato de bienestar anterior. No hay migraciones.

## Respuestas diarias — 2.3

Las rutas actuales son `GetSnapshotAsync` (home móvil y snapshot ERP) y
`GetEnrollmentWeekDetailAsync` (día dentro del detalle semanal). Ambas usan el
resolvedor común y `ProgramDayMissionPolicy` para las misiones exigibles.
Descanso omite ejercicio y el detalle lo declara mediante `rest:true` en el día.
Rutina, nombre y puntos de la tarea coinciden; las otras misiones se conservan.
Snapshot semanal vacío usa la plantilla en ambas rutas, sin escribir datos.
Las pruebas parametrizadas PostgreSQL cubren rutina explícita, descanso, JSON
antiguo y snapshot vacío. Se mantienen las guardias de inscripción/paciente de
los endpoints existentes; no se añade ningún identificador ni acceso clínico.

## Completación, máximo y racha — 2.4

La escritura, el replay y el snapshot calculan el máximo sobre las misiones
exigibles del día y la base vigente de DAY_BONUS. En descanso no se pueden
completar ejercicios ni recibir su XP; la ausencia de ejercicio no deja puntos
imposibles en el denominador. Completar las otras misiones alcanza el 100% y
otorga el bonus una vez. Sin misiones, el máximo es cero.

La racha califica por umbral o por completar todas las misiones exigibles,
incluido un día de descanso con menos tareas que `streak_min_tasks`. Un día
incompleto bajo el umbral no aumenta la racha ni gana bonus. El modelo de rescate
y el reconciliador incluyen también los días perfectos persistidos, para no
borrar después la calificación de un descanso completo. No se altera el decaimiento
temporal de las rachas vencidas ni se añaden XP ficticias por ejercicio.

## Próximos seis días y contrato — 2.5

`GET /api/v1/program/me/snapshot` y la lectura autorizada de snapshot ERP conservan
los campos anteriores. Añaden `upcoming` y `rest` opcionales; lectores de respuestas
anteriores deben tolerar su ausencia. `rest` solo se serializa cuando es true.

```json
{"rest":true,"upcoming":[{"date":"2026-11-03","day":2,"weekNumber":1,"rest":true,"missions":[{"type":"podcast","title":"Episodio","mediaId":"UUID","contentUnavailable":false}]}]}
```

`upcoming` va de mañana a hoy+6 en la fecha local recibida del handler. Cada día
incluye `date`, día absoluto de programa `day`, `weekNumber`, `missions` y `rest`
opcional. Cada misión declara `type`, `title` y, según el contenido, `routineId`,
`mediaId`, `nutritionPlanId` o `sourcePrescriptionId`. Los IDs ausentes se omiten.
`contentUnavailable` indica un hueco real; no se presenta el demo como publicado
válido. Descanso no lleva misión de ejercicio. Al terminar el programa se devuelve
una lista vacía, nunca día 84 para las doce semanas; el legado conserva 581 días.

La vista previa usa la misma mezcla de plantilla e individuales que la activación.
Las semanas iniciadas conservan su snapshot; las futuras rehacen únicamente filas
de programa y conservan decisiones individuales, incluso las de origen antiguo.
El cálculo es puro: no activa ni materializa semanas. Catálogos y nombres se cargan
por conjuntos, sin SQL por día ni por semana. Todas las cargas quedan acotadas a
la inscripción ya autorizada; un snapshot de otro paciente no participa. Las
pruebas cubren ambos casos, límite, compatibilidad JSON y número constante de lecturas.
