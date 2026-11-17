# Auditoría general del ERP

`GET /api/v1/audit/activity-log` requiere autenticación y el permiso `Audit.View`.
Lee `audit.activity_logs` en todos los esquemas; la ruta del programa conserva su
alcance y permiso actuales. Auth ya siembra `Audit.View` y lo asigna al Admin.
No agrega migraciones ni escrituras.

Parámetros opcionales:

| Parámetro | Contrato |
| --- | --- |
| `page`, `pageSize` | Predeterminados 1 y 20; página ≥ 1 y tamaño entre 1 y 100. |
| `schema`, `table` | Coincidencia exacta, quitando espacios externos. |
| `action` | `INSERT`, `UPDATE`, `DELETE`, sin distinguir mayúsculas. Sin filtro incluye también eventos semánticos existentes. |
| `actor` | El correo contiene el texto literal, sin distinguir mayúsculas. |
| `from`, `to` | Instantes ISO 8601 con offset, ambos inclusivos; `from` ≤ `to`. |
| `search` | `recordId` o `correlationId` contiene el texto literal; distingue mayúsculas. |

Los parámetros inválidos devuelven 400. La ausencia de sesión devuelve 401 y la
ausencia de `Audit.View`, 403. El resultado es `{ items, total, page, pageSize,
totalPages }`, ordenado por `occurred_at DESC, id DESC`. Las páginas fuera del
total devuelven `items: []` conservando la paginación solicitada. Un total vacío
tiene `totalPages: 0`.

Cada elemento expone exclusivamente `id`, `occurredAt`, `action`, `schemaName`,
`tableName`, `recordId`, `actorType`, `userEmail`, `userRole`, `ipAddress` y
`correlationId`. `action` y `actorType` se devuelven en mayúsculas. Los cuatro
últimos campos de actor/correlación permiten null. Nunca se seleccionan ni
devuelven `old_data`, `new_data`, `changed_data`, `metadata`, `user_id` o
`request_id`.

La consulta usa proyección EF sin seguimiento, filtros SQL y cancelación de
extremo a extremo. Se conserva paginación por offset con total por el contrato
pedido para el ERP; en páginas profundas y búsquedas parciales el costo crece.
El índice existente por `occurred_at` ayuda al orden; no se modifica el esquema.
El total y los elementos se leen por separado, por lo que escrituras concurrentes
pueden alterar el total entre lecturas.

Validación:

```powershell
dotnet build CoppAddresd.slnx
dotnet test tests/CoppAddresd.UnitTests --filter FullyQualifiedName~AuditActivityLog
dotnet test tests/CoppAddresd.IntegrationTests --filter FullyQualifiedName~AuditActivityLogPostgresTests
```

La integración requiere `COP_TEST_DB_CONNECTION` local con permiso de crear BD;
crea y elimina únicamente `audit_option_b_test_<uuid>`, con tabla sintética propia.
Comprueba filtros y límites inclusivos, valores históricos, orden con empate,
paginación extrema, cancelación y ausencia de payloads en el SQL. No aplica
migraciones ni usa filas de la BD de origen. Sin la variable se omite de forma
explícita. El test HTTP usa controlador, autorización y validación reales con
repositorio sustituido: acredita 401/403/200 y contrato, no un login real.

Verificación local de esta entrega (17-11-2026): 18/18 pruebas unitarias/HTTP y
3/3 pruebas contra PostgreSQL efímero propio pasaron. La integración comprobó
la traducción real del filtro de acción tanto para `INSERT` del trigger como
para `Insert` histórico. `dotnet build CoppAddresd.slnx` terminó sin errores;
el último build incremental no emitió advertencias. El PostgreSQL de pruebas
se detuvo y no se modificaron servicios ni datos del stack compartido.
