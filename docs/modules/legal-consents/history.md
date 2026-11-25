# Historial de aceptaciones de cuenta

Las dos operaciones requieren una sesión del ERP. La consulta requiere *LegalAcceptances.View*. La descarga requiere *LegalAcceptances.Export*. El rol Admin recibe ambos a través del registro de permisos. Los roles personalizados reciben cada permiso según el ámbito de su asignación.

| Método y ruta | Resultado |
| --- | --- |
| `GET /api/v1/legal-acceptances` | Página JSON con `items`, `total`, `page` y `pageSize`. El orden es fecha descendente e ID de recibo descendente. |
| `GET /api/v1/legal-acceptances/export` | Archivo `legal-acceptances.csv` con los mismos filtros y orden. UTF-8 con BOM y fechas en UTC. |

Los filtros son `accountId` (UUID), `documentCode` (código exacto del catálogo), `version` (por ejemplo, `2.0`), `from` (inicio incluido) y `before` (fin excluido). Las fechas se envían como ISO-8601 con zona. La consulta acepta `page` desde 1 y `pageSize` de 1 a 100 (25 por defecto). La descarga aplica el alcance y todos los filtros sin paginar. Al superar 5000 registros devuelve 422 con `LEGAL_HISTORY_EXPORT_LIMIT` para que el operador acote el intervalo o la cuenta.

Cada fila incluye `receiptId`, `accountId`, `scope`, `documentCode`, `documentTitle`, `versionId`, `version`, `locale`, `acceptedAt`, `ip` y `userAgent`. Los datos técnicos ausentes de un recibo anterior se devuelven como `null` en JSON y vacíos en CSV. Las filas corresponden a Términos y Privacidad de cuenta. Los recibos demo históricos se muestran cuando su documento y su variante coinciden.

El ámbito activo usa `X-Clinic-Id` o `X-Organization-Id`. La clínica del perfil se valida en el servidor. Con una asignación profesional se requieren pacientes vinculados activamente. Con un permiso global, Admin puede auditar recibos que carecen de perfil activo cuando opera en contexto global. La exportación registra un evento `LegalAcceptanceHistoryExport` con el operador, el ámbito y el número de filas.

La API responde 403 ante una audiencia o permiso inadecuados. Un rango invertido devuelve `LEGAL_HISTORY_INVALID_RANGE` y una versión con formato inválido devuelve `LEGAL_HISTORY_INVALID_VERSION` (HTTP 400).
