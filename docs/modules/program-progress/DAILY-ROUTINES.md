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
