# Asignación de podcasts a varios pacientes — 1.15

`POST /api/v1/program/content/bulk-assign-patients` requiere `Program.Edit`
(matriz de permisos: Admin y Director clínico). El guard exige además paciente
asignado; Admin solo exceptúa la asignación y nunca el permiso.

Entrada: `{patientIds:[UUID],mediaId:UUID,semanaDesde:2,semanaHasta:4}`.
Admite de 1 a 100 identificadores, semanas 1–83 y hasta la duración de cada
programa propio. Deduplica pacientes. El medio debe ser un podcast publicado,
con archivo válido y distinto del demo. Actor y fecha proceden de la sesión.

Salida: `{aplicadas:6,omitidasSemanaIniciada:2,omitidasSinAlcance:[UUID,UUID]}`.
Los dos números cuentan pacientes distintos: un paciente con rango parcialmente
iniciado puede aparecer en ambos conteos. `aplicadas` incluye selecciones ya
iguales para conservar el resultado al reintentar. Los ajenos no se cargan ni se
modifican; solo se devuelven identificadores enviados, sin nombres ni datos.
Un id inexistente se trata igual que uno sin alcance. Si un paciente propio no
tiene inscripción activa, falta la tarea podcast de un día o el rango sale de
su programa, devuelve 422 sin escribir parcialmente el lote.

Conserva semanas iniciadas por fecha local, estado, activación o completado.
Las futuras pueden existir vacías o no existir: añade sus podcasts con puntos y
orden de la tarea de plantilla (semanal antes que genérica). No inventa puntos.
El programa de doce semanas omite el día 84; el legado de 83 semanas conserva
su duración. No cambia las otras tareas. Los podcasts quedan `individual` con
`edited_by/edited_at`, y sobreviven a la activación. Repetir una selección idéntica
no reescribe snapshots ni fechas. Funciona con la plantilla de inscripción archivada.

`PodcastSnapshotAssignment` reutiliza la sustitución de `BulkAssignProgramContent`;
la ruta individual anterior conserva su contrato. La ruta nueva carga pacientes,
inscripciones, plantillas y semanas por conjuntos, sin SELECT por paciente/día.
Una transacción serializable persiste el plan completo; la estrategia Npgsql
puede reevaluarlo al reintentar. Cambios concurrentes no recuperables dan 409.
No se escribe en pacientes ajenos aunque el lote incluya ids propios y ajenos.
