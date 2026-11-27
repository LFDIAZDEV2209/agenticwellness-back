# KAN-170 · Evidencia por fuente y cierre

Esta guía acompaña la [decisión operativa](retention-proposal.md). Separa las
acciones ejecutadas por el servidor, las verificaciones de operadores y los
hallazgos de infraestructura. No activa ninguna acción de producción.

## Revisión de una solicitud

Con la identidad del titular verificada, el operador ERP asignado usa el
comprobante y las claves internas del titular para pedir evidencia a cada
custodio. Antes de ejecutar o registrar una acción debe identificar las
réplicas, versiones y destinatarios afectados, decidir qué conservar y
documentar la regla y cualquier suspensión. No registrar identificadores de
paciente, textos clínicos, claves de objetos, tokens o credenciales en Jira
ni en los campos de referencia del comprobante.

- `PUT /api/auth/privacy/deletions/{id}/device-tokens` borra únicamente los
  registros de `app.device_tokens` del titular verificado. Requiere `userId`,
  `confirmedEligible: true` y `evidenceReference`. La ejecución y el asiento
  de evidencia son atómicos. No borra registros de Firebase ni de Apple.
- `PUT /api/auth/privacy/deletions/{id}/sources/{source}` registra una
  comprobación externa al servicio Auth. Requiere `userId`, `outcome`,
  `remainingCount` y una `evidenceReference` opaca. El asiento es inmutable
  por comprobante, fuente y referencia. Una revisión usa otra referencia.
- `absent` requiere conteo cero verificado, no falta de acceso. `erased`
  requiere conteo cero y `actionReference`. Para un proveedor, es su acuse
  o verificación posterior a la ejecución, no el envío de la solicitud. Para
  un conjunto protegido requiere también `policyReference`.
- `retained` requiere conteo positivo, `policyReference` y `reviewAt`
  futura. La prueba debe identificar custodio, fundamento, suspensión y
  disposición de los demás registros elegibles, si los hubo. No usar
  `retained` para un trabajo pendiente. Un archivo existente requiere
  `archiveReference` y `restoreReference`.
- Una copia de respaldo usa `expired` solo después de comprobar que ya no
  quedan copias recuperables afectadas y aportar referencias de expiración y
  restauración. No usar `erased` en sustitución de esa comprobación. Una
  copia que deba conservarse se registra con la excepción y su revisión.
- `PUT /api/auth/privacy/deletions/{id}/completion` recibe `userId`,
  `inventoryReference` y `restoreReference`. Devuelve HTTP 409 con
  `missingSources` si falta una fuente, venció una excepción registrada o
  reaparecieron preferencias o tokens en el primario. Guarda el cierre y
  el operador en la misma transacción. Las referencias no se muestran al
  paciente. El servidor comprueba la estructura y cobertura del expediente,
  no inspecciona los documentos u operaciones de terceros.

## Mapa de fuentes y evidencia esperada

| Clave | Qué inventariar y verificar |
| --- | --- |
| `auth-preferences` | `auth."UserPreferences"` por `UserId`. Ejecución propia y lectura de cero al cierre. |
| `app-device-tokens` | `app.device_tokens` por `user_id`. Ejecución propia y lectura de cero al cierre. |
| `identity-profile` | Auth: cuenta, accesos por aplicación, sesiones y refresh tokens, OTP, invitaciones, comunicaciones y perfil de paciente. Confirmar revocación de acceso de paciente sin afectar un acceso ERP separado, tratar identificadores elegibles y conservar el nexo clínico necesario. La baja heredada no demuestra toda la disposición. |
| `wellness-legacy` | Check-ins, asignaciones, planes, semanas, XP, rachas, rutinas y recetas en `app`. Conciliar el vínculo legacy y las copias activas o migradas, sin tocar catálogos o plantillas compartidas. |
| `wellness-motor365` | `app.person`, `consent`, `screening`, `daily_log`, `plan_snapshot`, `engine_event`, `audit_log` y `professional_review`. Buscar tanto por la persona canónica como por el vínculo de origen y separar consentimiento, auditoría y revisión clínica. |
| `consumer-vitals` | Datos de bienestar propios, incluido `app.vital_signs` cuando corresponda. Determinar si existe otra copia incorporada al expediente y tratarla con el custodio clínico. |
| `clinical-records` | Expediente, encuentros, mediciones clínicas, resultados, documentos y objetos privados asociados. El custodio confirma jurisdicción, vigencia, excepciones y fechas. No borrar por coincidencia de un ID de identidad. |
| `telemedicine` | Solicitudes, citas, salas, sesiones, webhooks, alertas y encuentros en `tele`. Cruzar IDs al proveedor de video y conservar registros clínicos aplicables. |
| `community` | Perfil, publicaciones, reacciones, adjuntos, moderación e informes en el servicio Community. Verificar referencias de terceros, auditoría y objetos, no eliminar contenido ajeno. |
| `ai-service` | Historial y checkpoints PostgreSQL, hilos y conversaciones de voz, memoria (incluidas colecciones vectoriales si se usan), Vita y sus recibos. El borrado por hilo en AI Service no certifica cobertura por titular. Inventariar todos los hilos y memorias por propiedad. |
| `food-ai-storage` | Historial, análisis, adjuntos de fotos y derivados en Food AI y almacenamiento privado. Vincular servicio, objetos y proveedores de inferencia, incluidos resultados persistidos. |
| `audit-consents` | `audit.activity_logs`, consentimientos, recibos y entregas. Separar carga de exportación protegida en `auth."PrivacyExportArtifacts"` (24 h de disponibilidad) de metadatos de evidencia y copias históricas. Revisar valores antes/después, conservación y suspensiones. |
| `service-logs` | CloudWatch de los servicios **en ejecución**, traces y destinos secundarios, incluidos viejos grupos, DB ops y reglas de exportación. Verificar 30 días en logs ordinarios y clasificar excepciones de incidentes o auditoría por separado. |
| `openai` | Operaciones de modelos, voz o activos persistidos que estén habilitados en las cuentas efectivas. Verificar proyectos y objetos relevantes y registrar disposición, ausencia o conservación confirmada. |
| `anthropic` | Inferencia o activos entregados por los flujos configurados. Confirmar cuenta efectiva, tratamiento y copias sujetas al acuerdo vigente. |
| `elevenlabs` | Audio, historial y activos de voz que correspondan al titular. Obtener comprobación del proveedor por cuenta y función. |
| `twilio` | Verify/OTP, SMS, Voice y Video; tratar según producto los mensajes, grabaciones habilitadas y sus identificadores, sin incluirlos en Jira. |
| `whatsapp-meta` | WhatsApp Business y su intermediario cuando aplique. Distinguir copias de la aplicación, webhook y registros bajo el control de cada encargado. |
| `transactional-email` | Cuenta SMTP (documentada como Microsoft 365 en el despliegue), colas, entregas, buzones y cualquier destino de terceros. Revisar copias operativas y las que requieran conservación. |
| `mobile-push` | Proyectos Firebase/FCM y Apple/APNs usados en el despliegue. Verificar pulsos, token, preferencias y cualquier historial/payload persistido por destino. La fila local no es prueba del proveedor. |
| `retired-archives` | Inventario KAN-118/KAN-122, dumps y objetos de módulos retirados. Congelar escrituras, inventariar por fuente, capturar conteo y SHA-256, comprobar restauración aislada y reconciliar los borrados posteriores antes de retirar la copia activa. El archivo no reinicia el plazo original. |
| `rds-pitr` | Ventana automática y último punto afectado. Verificar expiración longitudinal y reconciliación en una restauración aislada. |
| `rds-manual` | Cada snapshot propio, compartido o copiado. Definir custodio y fecha por copia, con excepciones de recuperación y del expediente. Separar snapshots de archivo. |
| `s3-versions` | Objetos y todas sus versiones y marcadores en buckets activos, antiguos y secundarios. Verificar si una versión pasó a no actual y clasificar antes de caducar. No borrar objetos actuales ni basar la fecha solo en la carga de la versión. |
| `external-backups` | Copias externas al PITR, snapshots y versiones ya enumerados, incluidos dumps operativos locales, proveedores, otra región/cuenta y otras rutas de exportación. Confirmar alcance y disposición con cada custodio. |

Cada fila puede representar varias fuentes físicas. Los resultados agregados
requieren un manifiesto que enumere cada una. Si aparece un destino nuevo,
incorporarlo al manifiesto e inventario antes de declarar cobertura. Si el
titular pidió exportación y baja, entregar la copia solicitada por el canal
autorizado antes de revocar acceso o retirar la carga exportada.

## Evidencia observada en AWS · 27-11-2026

Consulta de solo lectura, sin descargar contenido ni modificar políticas.
El [preflight AWS por recurso](cloud-retention.md) protege la copia de
recuperación del corte legado y separa versión no actual, antigüedad y
elegibilidad comprobada.

| Fuente | Observado | Para cerrar |
| --- | --- | --- |
| RDS PostgreSQL activo en Ohio | Automático activo, cifrado y de 7 días. Ventana reportada del 20 al 27 de noviembre. | Ya coincide con la ventana acordada. Aún no hay evidencia de restauración y reconciliación para una copia de producción. |
| Snapshots manuales | Tres snapshots cifrados del RDS activo, del 18 y 25 de noviembre, y un snapshot cifrado separado de la instancia de archivo, del 26. Las vistas consultadas no mostraron etiquetas de disposición. No coincide con el recuento del 25 de noviembre. | Cotejar el cambio de inventario en el historial operacional y asignar disposición por copia. El snapshot de archivo no es evidencia de que se archivaron todos los módulos. |
| S3 principal de la API y Community | Versionado, sin lifecycle, replicación ni Object Lock configurados. Inventario completo devuelto de 166 versiones de datos actuales, cinco no actuales y nueve marcadores de borrado. | Clasificar las cinco versiones junto con sus custodios y aplicar la política de 30 días solo a las elegibles. Confirmar expiración y recuperación posterior, sin cambiar objetos actuales o protegidos. |
| S3 secundario de almacenamiento y bucket de backups | El secundario no devolvió objetos, versiones ni marcadores, aunque tiene un lifecycle general de 90 días para versiones no actuales. El bucket de backups no devolvió objetos y no tenía lifecycle ni versionado configurados. Sin AWS Backup vaults visibles en las tres regiones consultadas (Ohio, Virginia y Oregón). | Confirmar que el secundario está efectivamente fuera de servicio antes de usarlo y cotejar dumps locales, cuentas/regiones adicionales, copias compartidas y proveedores. El resultado regional no acredita ausencia global. |
| CloudWatch | Las definiciones ECS activas apuntan a los grupos `/ecs/cooppadresd-*` sin retención configurada, al menos Auth, AI, API, Food AI y Community. Los grupos `/ecs/coppadresd-prod-*` sí tienen 30 días, pero no son prueba de retención para los servicios activos. | Clasificar conservación de incidentes/auditoría, y aplicar/verificar 30 días en los destinos ordinarios realmente usados. |

### Restauración y prueba del flujo

La prueba Playwright con PostgreSQL aislado verificó que el comprobante no
cierra al faltar el acuse de un encargado o una fuente. Probó rechazo de
retención clínica sin regla y fecha, y de expiración o retiro de un archivo
sin sus evidencias. Solo se ejecutó borrado de preferencias y registros de
dispositivo de un titular sintético. Los demás resultados de fuente eran
comprobantes sintéticos, no confirmaciones de terceros.

En el ensayo de restauración, un `pg_dump` íntegro previo al borrado se
restauró en una base aislada. Con el registro de borrados leído del primario,
la comprobación detectó una preferencia y dos tokens reintroducidos.
Bloqueó la restauración con ambos pendientes y volvió a bloquearla tras
reaplicar solo las preferencias. Pasó después de reaplicar el segundo paso.
El contenido clínico y las filas de otra cuenta se mantuvieron intactos.

En producción se necesita un registro externo a la copia, con corte reciente
y cobertura demostrada, para generar comprobaciones fiables. Una lista vacía
no es prueba de ausencia. No abrir la restauración a tráfico o jobs hasta
reconciliar PostgreSQL, objetos, archivos y los destinos afectados. Conservar
el informe y la decisión sobre excepciones aparte del sistema restaurado.
