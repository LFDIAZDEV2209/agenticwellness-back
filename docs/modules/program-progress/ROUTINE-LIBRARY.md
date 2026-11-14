# Biblioteca de rutinas — Fase 2

## Contraindicaciones y copia — 2.13

`exercise_routines.contraindications` es texto nullable. Crear/editar acepta
`contraindications` opcional, máximo 4000 caracteres. Se devuelve en detalle y
lista. En PUT, ausente o null conserva el valor para compatibilidad con clientes
anteriores; una cadena vacía lo borra. La creación y la copia conservan el texto.

`POST /api/v1/wellness/exercise-routines/{sourceRoutineId}/clone` requiere
`Wellness.Manage` (Biblioteca editar). Cuerpo opcional: `{name?,patientId?}`.
Responde 201 con el DTO de la copia y Location. Sin nombre usa «Copia de …».
Copia descripción, parámetros y contraindicaciones, ejercicios completos y sus
demostraciones por referencia; crea IDs nuevos y atribuye el actor del servidor.
No duplica archivos, medios ni asignaciones del original; el original queda intacto.

Sin paciente crea un borrador del catálogo compartido. Con paciente reutiliza
el modo personalizado de creación existente: activa la copia y su asignación
en una transacción. Comprueba permiso y paciente asignado antes de leer la fuente
o insertar; ajeno/inexistente → 404 sin copia parcial. Admin exceptúa la asignación,
nunca el permiso. Fuente inexistente → 404. Esta operación no cambia semanas ni
el origen de ítems de programa.

### Migración y reversa

`AddExerciseRoutineContraindications` agrega solo la columna nullable sin DEFAULT,
sin reescribir filas. Lock timeout de 5 s; compatible con binarios anteriores.
Down es de mantenimiento, bloquea la tabla y rechaza retirar la columna si hay
alguna advertencia, para no perder contenido. No se aplica Down automáticamente.
Las pruebas PostgreSQL verifican Up/Down/Up con 10.000 filas, el rechazo con datos,
la copia profunda de ejercicios/demostraciones y los casos propio/ajeno/Admin.

## Demostraciones externas — 2.14

Semilla y manifiesto en `scripts/seed_exercise_reference_media.py` y
`scripts/exercise_reference_videos.json`. Usa los cuatro videos editoriales de la
app y claves de `ExternalMediaRules` (1.8). Solo enlaza ejercicios sin demostración
y con un alias explícito; no sustituye variantes clínicas ni medios configurados.
Es de solo lectura por defecto y la segunda aplicación no cambia datos.
Procedimiento e informe en `scripts/README-exercise-reference-media.md`.
El snapshot sirve los externos publicados mediante `referenceVideo` (iframe),
sin enviar claves externas al proxy de storage. Conserva atribución editorial
para las cuatro referencias y respeta el retiro de medios configurados.
