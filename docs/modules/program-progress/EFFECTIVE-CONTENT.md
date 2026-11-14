# Resolución efectiva de contenido — 1.9

`EffectiveDayContentResolver` es la precedencia común de podcast, rutina/descanso y
nutrición. `EffectiveDayContentStore` precarga por conjuntos; resolver un calendario
no ejecuta consultas. No exige completar tests y admite inscripciones de plantillas
archivadas, incluidas las 581 fechas del legado `default-83w`. El programa de doce
semanas termina en el día 83. Una semana futura vacía se resuelve contra la plantilla.

## Fuentes y validez

- Podcast: snapshot individual → plantilla semanal → genérica. Solo podcasts
  publicados con archivo válido, excluyendo el audio demo.
- **Decisión de Luis, 13-11-2026:** la semana iniciada conserva su podcast publicado
  del snapshot, también si su origen es `program`. Cambiar la plantilla de A a B
  mantiene A en esa semana y aplica B a futuras. Un medio retirado continúa por
  la cadena de respaldo. Las fuentes son `individual`, `snapshot`, `template_week`,
  `template_generic` o `missing`.
- Rutina: descanso explícito terminal → snapshot válido → plantilla semanal/genérica
  → asignación antigua vigente. Solo rutinas activas. Fuentes: `rest`, `snapshot`,
  `template_week`, `template_generic`, `legacy` o `missing`.
- Nutrición: prescripción propia publicada/aprobada vigente → continuidad del ciclo
  anterior hasta la fecha de inicio de su sucesora publicada → respaldo del motor
  → asignación antigua propia aprobada. Continuidad repite el último menú del ciclo.
  Publicación y vigencia se comparan en la fecha local de la inscripción.
  Fuentes: `prescription`, `continuity`, `engine`, `legacy` o `missing`.
- El respaldo solo acepta planes generales activos. La recomendación debe pertenecer
  al paciente de la inscripción y estar aplicada/editada o en fallback. Su decisión
  final `appliedContent` prevalece sobre la propuesta original, incluso si está vacía.
  Sin selección aplicada se usa el respaldo general de la plantilla.
- El legado usa `NutritionPlanReviewRules.MenuDay`: días transcurridos y duración
  7/30, sin equiparar un menú de treinta días con el día de semana.

## Consumidores y activación

La misión diaria, el detalle semanal, el cierre de tareas, Mi plan y el contexto de
IA consumen esta resolución. `nutritionPlanId` sigue reservado a planes; una
prescripción se identifica mediante `sourcePrescriptionId`, sin mezclar tablas.
El detalle semanal incluye fuente y descanso; los DTO existentes conservan campos.
La activación usa el mismo componente después de combinar plantilla e individuales;
solo materializa referencias `program`, registra avisos de huecos y no copia el legado.
La cancelación se propaga al almacenamiento y al recorrido del calendario.

## Migración y verificación

`20261113205636_AllowThirtyDayLegacyNutritionCompletions` amplía el CHECK de menú
de tareas completadas a 1–30. Up valida un CHECK auxiliar sin bloquear escrituras
durante el escaneo; después intercambia constraints. No reescribe filas. Down está
comentado, exige ventana de mantenimiento y se niega a reducir a siete si hay menús
8–30; no borra ni transforma esos registros. PostgreSQL prueba Up/Down/Up con
10.000 filas y el rechazo de la reversa cuando existe un menú 8.

Las pruebas cubren cada clase y precedencia, semanas vacías/no materializadas,
plantilla archivada, continuidad y sucesora, publicación local, referencias ajenas,
legado 7/30, ausencia de tests, cancelación, activación por ambas rutas y consumidores.
El número de SELECT es constante para uno y 581 días; no hay consultas en el recorrido.

## Vista previa de publicación — 1.11

`POST /api/v1/program/templates/{id}/publish?preview=true` devuelve:

```json
{"pacientesQueCambian": 12, "conPersonalizacionesFuturas": 3, "semanasOmitidasIniciadas": 18}
```

El modo normal conserva la respuesta de publicación existente. Vista previa no
publica, no incrementa versión ni modifica snapshots. Requiere `Program.Edit` y
el guard de alcance: Admin ve todos los activos; otros actores solo pacientes con
asignación profesional activa. Sin permiso, incluso Admin recibe 403.

`pacientesQueCambian` cuenta pacientes distintos que recibirán contenido de la
plantilla desde alguna semana futura; es un conteo de recepción, no una diferencia
contra un borrador histórico (el editor guarda las filas actuales). Se simula la
activación quitando únicamente filas `program` futuras y se usa el resolvedor.
`conPersonalizacionesFuturas` cuenta pacientes distintos con filas individuales
que se conservarán; los orígenes antiguos desconocidos se preservan. Los dos
conteos pueden solaparse. No se cuenta el día 84. `semanasOmitidasIniciadas` cuenta
semanas excluidas por estado/activación/completado o por haber alcanzado su fecha
local de inicio, incluyendo semanas anteriores. La inscripción agotada no recibe
contenido futuro. Las consultas filtran alcance antes de materializar o contar y
no se ejecuta SQL por paciente, semana o día.

## Validación al publicar — 1.10a

El modo normal de `POST templates/{id}/publish` exige tres contenidos válidos para
cada día 1–83, incluidos los genéricos de respaldo. Simula la cuadrícula con el
resolvedor, sin pacientes, prescripciones ni asignaciones particulares. No consulta
por día ni admite que el contenido de un paciente enmascare una plantilla incompleta.
Con huecos responde HTTP 422 `application/problem+json`, sin cambiar versión/estado:

```json
{"status":422,"detail":"TEMPLATE_CONTENT_GAPS: ...","gaps":[{"day":8,"weekNumber":2,"weekday":1,"taskCode":"podcast"}]}
```

`taskCode` usa `podcast`, `ejercicio` o `nut`. La lista ordena por día y clase.
Completar el contenido permite publicar e incrementa versión como antes. La plantilla
archivada mantiene su rechazo 409. El día 84 queda fuera de la simulación. Catálogos
compartidos y lecturas de pacientes reutilizan los mismos filtros de publicación,
actividad y propiedad de referencias. Esta tarea no crea migraciones.

## Consulta de huecos — 1.10b

`GET /api/v1/program/erp/content-gaps?page=1&pageSize=20` requiere `Program.View`
y alcance por paciente asignado (Admin ve todos). Devuelve `{items, total, page,
pageSize, totalPages}`; cada ítem contiene `patientId`, `enrollmentId`, `date`,
`weekNumber`, `weekday` y `missingClasses` (`podcast`, `ejercicio`, `nut`). Total
cuenta días con hueco, no clases. Orden estable por paciente, inscripción y fecha;
pageSize entre 1 y 100. Una página fuera del rango conserva total y devuelve items vacío.

Evalúa inscripciones activas desde hoy en su zona horaria hasta el último día;
excluye pasado, inscripciones agotadas y el día 84 del programa nuevo. El legado
archivado conserva su duración. Usa el resolvedor 1.9 y precarga por conjuntos:
snapshot futuro vacío no implica hueco y descanso explícito cubre ejercicio.
No escribe datos ni devuelve nombres, identificadores o conteos de pacientes ajenos.
La reparación 1.14 debe dejar este resultado en cero; 1.10b no repara por sí misma.

## Continuidad y versiones por fecha — 3.4

La nutrición delega la selección en NutritionPublicationTimeline, compartida con
la adherencia de ciclos. Precarga versiones históricas de ciclos que conservan
publicación, ordena por ciclo iniciado/publicación local/versión y usa el día
final del menú durante continuidad. La ausencia del menú en la versión vigente
activa el respaldo del motor; no revive una versión anterior con ese día.
Misión y lectura móvil permiten la versión histórica solo dentro de su paciente
y un ciclo todavía publicado. El resto de precedencias y el contrato sin
inscripción no cambian. Validación y evidencia: nutrition/CYCLES.md.
