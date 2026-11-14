# Rutinas por día — Fase 2

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

Solo edita semanas Locked futuras, sin activación, completación ni historial.
Semana iniciada o pasada → 409 `PROGRAM_WEEK_STARTED`, sin cambiar historial,
XP ni snapshot. Respeta zona del paciente y los 83 días. Inscripción ajena → 404
con Program.Edit y paciente asignado, también dentro del handler; Admin exceptúa
asignación, nunca permiso. Rutina debe existir y estar activa. Los campos nuevos
son opcionales; el cuerpo del editor anterior sigue siendo válido.

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
