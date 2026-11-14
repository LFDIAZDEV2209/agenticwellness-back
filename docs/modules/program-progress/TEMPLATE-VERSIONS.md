# Nueva versión desde plantilla activa — 1.29

`POST /api/v1/program/templates/{id}/versions`, sin cuerpo, requiere Program.Edit.
Devuelve 201 con el mismo ProgramTemplateDto de creación/consulta y Location hacia
el borrador nuevo. La UI puede navegar a ese ID para editarlo. Fuente inexistente
→ 404; fuente que no está Active → 409 `TEMPLATE_NOT_ACTIVE`; sin permiso → 403.
La autorización se comprueba en API y handler antes de leer la fuente.

El catálogo actual es compartido: el alcance de la operación es exclusivamente
el ID de plantilla solicitado. No se buscan pacientes ni inscripciones, no se
copian filas de otras plantillas y CreatedBy es el actor del servidor. No existe
propiedad por paciente para este catálogo; no se inventa una excepción de alcance.

La copia tiene ID/código nuevos, estado Draft, `version=source.version+1`, nombre,
descripción, duración, umbral de racha y códigos esenciales copiados; PublishedAt
queda null y las listas son independientes. El código generado cabe en 40 caracteres.
No publica ni cambia la numeración de la operación de publicación existente.

Para el programa de 83 días copia **83 celdas físicas de podcast**, una específica
por semana/día, resolviendo específica antes que genérica de la fuente. La semana
12 contiene solo seis días. Las demás clases se copian tal como existen, genéricas
o específicas, con sus puntos, orden y referencias. Todas las filas tienen IDs nuevos.
No duplica medios/archivos ni repara audios demo o referencias incompletas: publicar
sigue pasando por la validación existente de las tres clases. Si la fuente no tiene
una celda de podcast para algún día, responde 422 sin crear un borrador parcial.
Los programas legados conservan su duración y su calendario completo.

La fuente activa, otras plantillas, inscripciones y snapshots permanecen intactos.
La escritura reutiliza UpsertTemplate con transacción y constraint COALESCE de 1.3;
dos copias son borradores distintos, sin sobrescribir una copia anterior. El borrador
se edita por los endpoints existentes de plantilla/semana. Los audios de prueba
permanecen intactos. El relleno editorial de 1.14 sigue siendo una tarea separada.
