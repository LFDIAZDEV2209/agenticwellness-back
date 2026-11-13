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
