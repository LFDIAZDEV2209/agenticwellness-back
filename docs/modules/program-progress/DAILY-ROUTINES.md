# Rutinas por día — Fase 2

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
