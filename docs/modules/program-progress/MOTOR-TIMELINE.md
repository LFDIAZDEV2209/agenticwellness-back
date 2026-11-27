# KAN-151 · Bitácora persistida del Motor 365

La bitácora de ERP consulta las decisiones existentes en `app.engine_event`. No recalcula el motor ni crea eventos al leer. El alcance de persona y `Program.Profile360View` son obligatorios en ambas rutas; se rechaza la audiencia de la app. Exportar exige además `Program.Export` en el alcance de la persona.

| Método | Ruta relativa a `/api/v1/program/erp/patients/{patientId}/motor-timeline` | Parámetros |
| --- | --- | --- |
| GET | `/` | `from`, `to` (fecha de registro, inclusiva, en UTC), `kind` (`ajuste`, `sin`, `meta`, `modo`), `page` (40 eventos por página) |
| POST | `/export` | Los mismos filtros de fecha y tipo, `lang` (`es` o `en`). Exporta todos los eventos de la selección, no solo la página. |

Una revisión del plan puede persistir de nuevo eventos de días previos. La bitácora muestra la última fila por día y familia, dando prioridad a la versión del plan, la versión de los parámetros y la fecha de registro. `sin` y `ajuste` son resultados de la misma decisión semanal. Los filtros se aplican después de elegir la última fila y no recuperan revisiones sustituidas. No se eliminan filas históricas. La fecha de registro y el día del plan son conceptos separados. Los campos de cada evento incluyen ID, versiones, hora y razones bilingües.

Cada exportación válida y no vacía agrega la acción `timeline.export` al registro de auditoría del motor con usuario, persona y hora. No se devuelve el CSV si no se puede guardar la auditoría. El CSV no incluye nombre ni ID de la persona y neutraliza prefijos de fórmula en campos de texto.

## Pendiente para cerrar KAN-151

- El esquema de decisiones distingue fase, meta y revisión semanal. No tiene tipos dedicados para pausa/reanudación ni publica todas las pausas de seguridad diarias como eventos. No se infieren eventos que no estén persistidos.
- El cierre diario guarda ejercicio completado, no una sesión detallada con fecha, ejercicios, duración y síntomas. Hace falta un contrato de escritura compartido y una identidad de sesión estable para impedir que la captura en ERP duplique la de la app.
- Las notas internas, contactos y seguimiento no se agregan en la bitácora de decisiones. Requieren almacenamiento y autorización exclusivos de ERP y pruebas que demuestren que no se reflejan en API, exportación ni UI de la persona.
