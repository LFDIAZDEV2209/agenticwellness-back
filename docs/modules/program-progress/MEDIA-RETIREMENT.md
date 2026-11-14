# Sustitución al retirar medios — 1.12

`GET /api/v1/media/{id}/retirement-impact` (`Media.View`) devuelve
`{pacientes, plantillas}` para el diálogo. Plantillas cuenta plantillas activas
distintas; pacientes cuenta personas con contenido futuro afectado, incluidos
los calendarios aún no materializados. El guard filtra pacientes asignados antes
de cargarlos; Admin ve todos. No se exponen pacientes ajenos ni sus conteos.

`POST {id}/unpublish`, `POST {id}/archive` y `DELETE {id}` aceptan
`?replacementMediaId=UUID`; los POST también aceptan `{replacementMediaId}` en
el cuerpo opcional. Conservan sus permisos Media.Publish/Archive/Delete. Sin
sustituto, una plantilla activa o semana futura que usa el medio produce 409
con `impact: {pacientes,plantillas}`. El sustituto debe ser otro medio publicado,
del mismo tipo, con referencia válida; error 422 si no cumple.

Sustituye las filas de plantillas activas y los ítems `program` de semanas futuras
no iniciadas dentro del alcance. No toca semanas iniciadas (estado, activación,
completado o fecha local), ítems individuales ni orígenes antiguos desconocidos.
Despublicar/archivar conserva el archivo. El resultado mantiene el DTO de medio
y añade `retirementImpact` con los conteos anteriores al cambio.

**Decisión de Luis, 14-11-2026:** DELETE permanece en 409 mientras queden
referencias históricas o individuales, incluso con sustituto. También bloquean
las referencias de plantillas no activas y de ejercicios. Ese 409 revierte toda
sustitución de esa petición; se puede usar despublicar/archivar. Solo un DELETE
sin referencias elimina metadatos y después borra blobs. La comprobación global
es solo de existencia para integridad, sin cargar ni exponer pacientes ajenos.

La transacción serializable incluye lectura, sustitución y cambio de estado o
borrado; la estrategia de ejecución del proveedor gobierna reintentos. No llama
a storage dentro de ella. Errores y cancelación revierten los cambios. El 409
por historia no deja plantillas sustituidas parcialmente. No hay migraciones.
