# Ejemplos anclados del ciclo — 3.8b

Los ejemplos apuntan a `nutrition_intake_logs.id`, sin copiar sus valores. Una
corrección posterior de la comida conserva el anclaje, la nota y el momento del
anclaje; la consulta devuelve siempre los macros actuales. No sustituye la
revisión diaria ni cierra el ciclo.

Rutas bajo `/api/v1/nutrition/cycles`:

- `POST {id}/highlights/{intakeId}` con `{ "note": "Ejemplo de desayuno" }`,
  nota opcional de hasta 2000 caracteres; respuesta 204.
- `DELETE {id}/highlights/{intakeId}`, respuesta 204 incluso si ya estaba desanclado.
- `GET {id}/highlights?page=1&pageSize=30`, respuesta `{ page, pageSize, total,
  items }`. Tamaño 1–100, orden por anclaje descendente y UUID como desempate.

Cada ítem contiene `intakeLogId`, `localDate`, `mealCode`, macros `recorded`,
`waterMl`, `pinnedBy`, `pinnedAt`, `note`. El detalle de foto/análisis se consulta
con la fecha mediante 3.7, que aplica su permiso y firma temporal. Esta lista no
contiene claves de almacenamiento ni otorga acceso a fotos. La nota solo se
proyecta con `Nutrition.NotesView`; con el resto de permisos de lectura queda null.

Pin/Unpin exigen `Nutrition.Manage` y `IPatientScopeGuard` tanto para ciclo como
para registro. Deben pertenecer al mismo paciente incluso si ambos están dentro
del alcance del actor. Pin valida que la fecha esté en el tramo transcurrido del
ciclo, incluida continuidad atribuible; un día futuro no es anclable. List exige
`Nutrition.View` y `Nutrition.RecordsView` sobre el ciclo. Ajeno → 404; Admin
exceptúa asignación, nunca capacidad ni pertenencia al mismo paciente.

Persistencia aditiva: `app.nutrition_cycle_highlights`, PK `(cycle_id,
intake_log_id)`, FKs Restrict, actor del JWT y `clock_timestamp()`. Pin usa upsert
bajo lock de paciente: dos solicitudes concurrentes crean un solo ejemplo.
Repetir Pin permite actualizar la nota y su autor/fecha; Unpin es idempotente.
La consulta de página y total comparte snapshot Repeatable Read READ ONLY.

Migración `20261114194459_AddNutritionCycleHighlights`, SQL Up/Down revisado y
Up aplicado solo en la BD local. Tabla nueva vacía. Reversa de mantenimiento
rechazada mientras haya ejemplos; conservarlos antes de retirarla. Up/Down
vacío y protección con datos se verifican en PostgreSQL efímero.

Comandos de validación:

```powershell
dotnet build --no-restore -p:OutputPath=bin/Validation38b/ -v quiet
dotnet test tests/CoppAddresd.UnitTests --no-build -p:OutputPath=bin/Validation38b/ -v normal
dotnet test tests/CoppAddresd.IntegrationTests --no-build -p:OutputPath=bin/Validation38b/ --filter 'FullyQualifiedName~NutritionPlanWorkflowTests' -v normal
```

Aplicación local: `Get-Content -Raw -Encoding UTF8 $env:TEMP/copp-38b-up.sql |
docker exec -i coppAddresd psql -v ON_ERROR_STOP=1 -U app_user -d coppaddresd`.
No se publica ni se modifica la rama de Fase 2.

Evidencia: build verde, unitarios **2564 aprobados/105 omitidos** y PostgreSQL
**112/112** (7 min17s), suite `NutritionPlanWorkflowTests` completa con Fase 1/F2,
ambas ConservaPreedicion, revisión diaria y ejemplos. El caso nuevo comprueba
pin concurrente único y PK, corrección sin perder anclaje, nota protegida,
pertenencia al mismo paciente, ajeno denegado, Admin, desanclado repetido,
permisos, paginación, cancelación y reversa protegida.
