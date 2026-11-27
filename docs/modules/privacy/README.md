# KAN-170 · Datos del servidor

[Ticket y decisión de alcance](https://diomedescerda.atlassian.net/browse/KAN-170) ·
[Decisión KAN-114](https://diomedescerda.atlassian.net/browse/KAN-114) ·
[Handoff KAN-157](https://diomedescerda.atlassian.net/browse/KAN-157)

**El canal recibe solicitudes, entrega copias acotadas de perfil y vitales,
y entrega la copia integral cuando un operador publica un paquete revisado.**
El flujo de borrado verifica comprobantes y permite un paso acotado sobre
preferencias no clínicas. No extrae automáticamente todos los almacenes,
no ejecuta borrado integral ni cierra cuentas. KAN-114 autorizó el trabajo.
La [decisión operativa de Diomedes](retention-proposal.md)
fija las metas de producto. KAN-170 requiere verificar la regla jurídica
aplicable por fuente antes de habilitar la disposición.

## Contrato de recepción

El gateway ya enruta `/api/auth/*` a Auth. Estas rutas requieren Bearer JWT de
`app` y acceso vigente a esa aplicación. El titular se obtiene del token, no
del cuerpo. Las respuestas no se almacenan en caché. Un usuario no puede
resolver el comprobante de otro aunque conozca su UUID.

| Operación | Resultado |
| --- | --- |
| `GET /api/auth/privacy/requests` | `{ export: Receipt \| null, deletion: Receipt \| null, downloadAvailable: boolean, fullExport: ReadyExport \| null }`, solo pendientes del titular. |
| `POST /api/auth/privacy/requests` | `{"kind":"export","format":"json"}` (o `csv`) o `{"kind":"deletion","confirmed":true}`. |
| `GET /api/auth/privacy/requests/{id}` | Devuelve el comprobante del titular o `404`. Cuando se verificó una solicitud de borrado, incluye `review.verifiedAt` y `review.dueAt`. |
| `GET /api/auth/privacy/requests/{id}/download` | Con un comprobante pendiente de exportación del mismo titular, devuelve la copia acotada. |
| `PUT /api/auth/privacy/fulfillment/{id}` | Un operador ERP con `Privacy.PublishExport` publica el expediente revisado para el titular. |
| `GET /api/auth/privacy/requests/{id}/full-download` | Entrega el expediente revisado en el formato solicitado al titular autenticado. |

`Receipt` contiene `id`, `kind`, `format` (nulo para borrado) y `receivedAt`
(UTC). El primer POST devuelve `201` y `Location`. Un reintento mientras exista
una solicitud pendiente del mismo tipo devuelve `200` con su comprobante
original y no modifica el formato. El índice parcial único en
`auth."PrivacyRequests"` evita duplicados incluso entre réplicas. El registro
no incluye texto libre, datos clínicos ni credenciales. La baja de Auth no lo
elimina en cascada. El ID del titular sigue siendo un dato de acceso restringido.

El canal se configura en Auth mediante `PrivacyRequests__Enabled=true`. Sin
esta variable las rutas devuelven `404` para sesiones autenticadas, la app
oculta la tarjeta y los controles locales de KAN-157 siguen disponibles. Para
desactivarlo no se revierte la migración ni se eliminan comprobantes. La
migración solo agrega la tabla y su índice. Su `Down` elimina la tabla, por lo
que se reserva para bases de prueba o instalaciones sin solicitudes registradas.

**Puesta en servicio pendiente.** Antes de habilitarlo en un entorno real,
asignar responsable de atención y verificar cómo resuelve identidad, recopila
las fuentes y registra una negativa. La descarga acotada no resuelve la
solicitud. El depósito de una copia revisada tampoco la cierra hasta servirla;
una solicitud de borrado permanece pendiente. No se promete un plazo legal.

## Descarga acotada de perfil y vitales

El interruptor adicional `PrivacyRequests__DownloadEnabled=true` indica
`downloadAvailable` en la lista. Por defecto está apagado. El archivo solo
incluye campos demográficos y de contacto seleccionados, signos vitales legacy
y mediciones del catálogo clínico de categoría `vital`. El alcance está escrito
en el archivo y en la app. No incluye notas, contactos de otras personas,
credenciales ni claves de almacenamiento. No recopila todavía bienestar,
expedientes, IA, auditoría u otros repositorios.

Las filas se vinculan por `app.patient_profiles.user_id` al sujeto autenticado
en una misma instantánea. No se aceptan identificadores de paciente en la
petición. Se devuelven como máximo 5.000 filas por cada fuente de signos
vitales. Si se supera el límite o no hay un perfil único, el endpoint devuelve
`409`, sin truncar ni marcar la preparación. El CSV usa filas por campo y
protege textos con prefijos de fórmula. El JSON conserva el valor original.
El último `ExportPreparedAt` se registra antes de devolver el contenido. No
demuestra que el paciente guardó el archivo ni cierra su solicitud.

## Entrega de un expediente revisado

El operador prepara los registros del titular fuera de la app, verifica su
identidad y coteja cada fuente del inventario. Publica por el endpoint ERP un
JSON con `userId` y un `datasets` con una entrada para cada categoría:
`profile`, `wellness`, `vitals`, `clinical`, `ai`, `service_logs`,
`audit` y `archives`. Cada entrada lleva `category`, `status`,
`evidenceRef` (referencia opaca y sin PII) y `records`. Los estados son
`included` con registros, o `no_data` o `excluded` con arreglo vacío.
Una exclusión requiere evidencia de revisión. La estructura no certifica por
sí sola que el operador consultó todas las réplicas o redactó datos de terceros.

El servidor comprueba titular, solicitud y cobertura, y transforma JSON o
CSV según el formato del comprobante. Registra operador, publicación,
caducidad y primera entrega. Una publicación no sobrescribe otra vigente.
El archivo se restringe al titular, no usa enlaces públicos y no se cachea.
Al servirlo por primera vez se cierra la solicitud, aunque sigue descargable
hasta caducar. El registro de entrega no acredita que el dispositivo lo guardó.
Un archivo que supere el límite de solicitud de 2 MiB requiere otro canal
operativo y no se trunca. El cliente no se ocupa de expedientes mayores.

`PrivacyRequests__FulfillmentEnabled` está apagado por defecto. Para habilitarlo
en un entorno autorizado, configurar una clave AES-GCM de 32 bytes en hex
por el gestor de secretos (`PrivacyRequests__ArtifactKey`, distinta del JWT).
La copia está cifrada en la tabla, ligada al comprobante y al titular. El tiempo
de disponibilidad del archivo (`PrivacyRequests__DownloadHours`, 24 h por
defecto) es operativo, no un período de retención legal. El trabajo periódico
retira la carga vencida del primario. Las copias históricas de la BD pueden
contener el cifrado hasta su expiración y necesitan su propia política.
La clave no se rota hasta que caduquen los archivos vigentes, salvo revocación
deliberada de las copias activas. No se habilitó en producción.

## Verificación y ejecución de borrado

Los operadores ERP con permiso `Privacy.ReviewDeletion` pueden actuar
únicamente con `PrivacyRequests__Enabled=true` y
`PrivacyRequests__DeletionEnabled=true`. El segundo interruptor está
apagado por defecto. La app no puede invocar estos pasos con un token de
paciente. El ID de titular aportado se verifica contra el comprobante
pendiente. No se aceptan nombres de tablas ni instrucciones SQL.

| Operación | Contrato |
| --- | --- |
| `PUT /api/auth/privacy/deletions/{id}/verification` | Requiere `userId`, `identityEvidence` y `policyReference`. Puede incluir `earlierDueAt` si la regla aplicable tiene una fecha anterior. Devuelve `verifiedAt` y `dueAt`. |
| `PUT /api/auth/privacy/deletions/{id}/preferences` | Requiere un comprobante ya verificado, `userId`, `confirmedEligible: true` y `evidenceReference` del cotejo de preferencias. Borra solo la fila `auth."UserPreferences"` del titular y devuelve `completedAt` y `rowsErased`. |

La fecha inicial se registra en el servidor. El vencimiento es el menor
entre verificación más 90 días y el plazo anterior aplicable que aportó el
operador con su referencia. Una corrección documentada solo puede acortar
el plazo. La verificación no autoriza el borrado por sí sola. Las referencias
son opacas y no llevan PII, tokens ni datos clínicos.

Al ejecutar el paso, el operador confirma que esas preferencias están
autorizadas para borrado y no tienen que conservarse. La supresión y el
asiento de evidencia comparten transacción. Los reintentos del mismo paso
devuelven el comprobante persistido, no vuelven a ejecutar. El proceso no
espera hasta la fecha límite. La app muestra verificación y vencimiento
del trámite sin mostrar las referencias internas ni confundir el paso de
preferencias con el borrado completo.

Los pasos de preferencias y tokens de dispositivos no cierran la cuenta,
no tocan el expediente ni cambian `AccountPurgeService` o la baja heredada.
La [guía de cierre por fuente](coverage.md) describe el comprobante de cada
almacén, los requisitos de archivo y restauración y las verificaciones
pendientes en producción. No usar estos contadores para declarar que se
borró el perfil completo.

## Inventario para la matriz de retención

La [decisión operativa](retention-proposal.md) fija hasta 90 días calendario
para la eliminación elegible desde la verificación, sin espera mínima y
sin sustituir un plazo aplicable anterior. No activa la ejecución.
Esta tabla es un mapa técnico inicial, no una política. Para cada fila faltan
confirmar el titular y los sistemas de custodia, el evento que inicia el plazo,
el plazo aprobado, las excepciones o suspensiones, la disposición y el
responsable. Una exclusión de exportación también requiere motivo documentado.

| Dato | Fuentes y claves a reconciliar | Disposición pendiente de aprobar o comprobar |
| --- | --- | --- |
| Identidad y perfil | `auth."Users"`, preferencias y acceso por aplicación, `app.patient_profiles.user_id`. | La baja existente calcula 90 días en Auth y anonimiza algunos campos según `AccountPurgeService`. Ese comportamiento no demuestra un plazo aprobado para KAN-170 ni anonimización integral. |
| Historial de bienestar | Check-ins, inscripciones, semanas, XP, rachas, nutrición y rutinas en `app`. KAN-122 conserva IDs legacy y KAN-130 integra las nuevas escrituras. | Exportar por propiedad verificada y reconciliar la versión activa y las archivadas. Separar lo generado por el usuario de catálogos compartidos. |
| Vitales y clínica | `app.vital_signs`, `app.clinical_measurements`, evaluaciones, expedientes y documentos en `app`, telemedicina en `tele`, objetos privados. | Identificar expediente y obligaciones por custodio. No asumir que borrar el perfil elimina copias, adjuntos, versiones o referencias clínicas. |
| IA | Conversaciones y checkpointer PostgreSQL de AI Service, estado de Vita, `app.patient_ai_briefs`, `app.vita_write_receipts`. | Resolver propiedad por usuario/paciente antes de exportar o borrar. Inventariar copias en proveedores, voz y observabilidad y confirmar qué se puede exigir a cada encargado. |
| Logs, auditoría y solicitudes | `audit.activity_logs`, `auth."PrivacyRequests"`, consentimientos, logs operativos por servicio, trazas y destinos externos. | Separar evidencias necesarias de logs operativos. Revisar también columnas de antes/después y redacción de datos sensibles. Acordar acceso y retención por origen. |
| Módulos retirados | Inventario KAN-118 de Chat IA, Historia Clínica, métricas, historial, Citas, Academia, INFINITO, Marketplace y Podcast. Reconciliación KAN-122. | Apagar escrituras primero. Archivar solo lo inventariado, generar manifiesto de conteos y sumas de comprobación, restaurar en aislamiento y reconciliar. No usar el cambio de estado de `default-83w` como prueba de archivo de todos los módulos. |
| Copias | RDS/PITR, snapshots manuales, S3 con versiones, y copias externas por verificar. | Ver inventario observado. Falta comprobar vencimiento, copias externas y reconciliación después de restaurar. |

## Inventario anterior de copias en AWS (25-11-2026, solo lectura)

La consulta más reciente del 27 de noviembre está en la [guía de cierre](coverage.md).

| Almacén | Observación | Lo que no demuestra |
| --- | --- | --- |
| RDS PostgreSQL de producción, us-east-2 | Cifrado. Ventana automática configurada en 7 días con punto restaurable reportado. | No establece el vencimiento de otras copias ni prueba una restauración segura. |
| Snapshots manuales de esa instancia | Cinco disponibles y cifrados. El más antiguo es del 07-09-2026, anterior a la ventana automática. | No se verificó una fecha de disposición para cada copia, ni si hay copias cruzadas o fuera de cuenta. |
| Bucket privado principal de objetos | Versionado habilitado. AWS devolvió ausencia de políticas de lifecycle y replicación en ese bucket. | Borrar el objeto actual no acredita la eliminación de sus versiones anteriores ni la ausencia de copias en otros sistemas. |

No se descargaron datos, no se restauraron copias de producción y no se
cambiaron configuraciones. La verificación disponible es evidencia de la
configuración observada, no una política aprobada. Para el ensayo final, usar
un restore aislado sin tráfico de pacientes, medir conteos e integridad y
reaplicar los borrados posteriores al punto de restauración antes de exponer
el entorno. Inventariar también los objetos/versiones y copias en proveedores.

## Preflight de disposición y de restauración

`scripts/qa/kan-170/retention_review.py` solo lee manifiestos locales y devuelve
conteos sin datos de paciente. No hay modo de ejecución destructiva.
`retention-policy.pending.json` sigue sin reglas ejecutables aprobadas y no
se usa para registrar la decisión de producto. El campo `days` del preflight
indica retención mínima, no fecha límite de un trámite.
Con `disposition`, se pasan `--policy`, `--records` y `--now` con hora UTC.
Las reglas requieren una referencia de aprobación y una fecha efectiva,
acción y días por categoría. Para acciones de baja se requiere fecha ancla
verificada y ausencia explícita de una suspensión. Los datos archivados no son
elegibles sin referencia, SHA-256 y evidencia de restauración. El preflight
valida forma y cobertura, no autentica la aprobación o el informe externo.

Con `reconcile-restore`, usar `--snapshot-at`, `--ledger` (comprobantes de
borrado con categoría, fuente y fecha efectiva) y `--findings` (lecturas
verificadas del entorno aislado por comprobante y fuente, con conteo de
registros). Las nuevas evidencias usan `source`, por ejemplo
`auth.UserPreferences`. Una lectura de cero filas en una fuente no cubre
otra fuente de la misma categoría. La evidencia histórica sin `source`
solo coincide con lecturas históricas sin `source`. Devuelve código
2 si faltan lecturas o reaparecen datos. En ese caso, no devolver tráfico al
entorno restaurado. Reaplicar solo acciones autorizadas y volver a medir.
Estas lecturas las ejecutan los custodios por fuente, no el script.

## Evidencia de cierre que falta

1. **Cobertura real de exportación.** El camino de entrega está implementado y
   probado con dos titulares sintéticos. Todavía hay que reconciliar la extracción
   de cada almacén real y sus exclusiones, y probarla en un entorno autorizado.
2. **Borrado y resguardo.** Obtener política aprobada por categoría. Probar
   confirmación, plazo, bloqueo ante una conservación obligatoria, revocación
   de sesiones si corresponde, reintento sin duplicar y evidencia por almacén.
   El flujo previo de baja confirmada de Auth no cambia con este ticket.
3. **Archivo y backup.** Para cada módulo y copia, registrar propietario,
   sistema, alcance y fecha de expiración. Restaurar en un entorno aislado,
   comprobar conteos e integridad y reconciliar solicitudes de borrado
   posteriores al snapshot antes de reabrir acceso. Mantener comprobante del
   lote y distinguir borrado primario de expiración de la última copia.
4. **Atención y cierre.** Acordar quién atiende la bandeja y quién confirma
   resultado, excepción y canal de comunicación con el titular. Registrar
   dichas evidencias en un sistema de acceso restringido y enlazarlas desde
   el ticket sin incluir PII.

## Reproducción local

Usar un PostgreSQL 17 descartable, solo con la base `copp_review_kan170`. Iniciar
Auth en `127.0.0.1:5127` con `Jwt__Issuer=copp-kan170-local-qa`, un secreto
sintético en archivo privado, `PrivacyRequests__Enabled=true` y la conexión a
esa base. Auth aplica las migraciones a la primera ejecución en ese entorno.
Cargar `scripts/qa/kan-170/synthetic-accounts.sql` después del arranque. El script
reinicia solamente los comprobantes de tres IDs de QA en esa base.

En el worktree de la app ejecutar `npm run dev -- --host 127.0.0.1 --port 5187`.
Luego ejecutar `COP_PRIVACY_QA_SECRET_FILE=/ruta/privada node e2e/privacy-requests.mjs`.
Para incluir la descarga, habilitar también `PrivacyRequests__DownloadEnabled=true`,
aplicar `scripts/qa/kan-170/synthetic-export.sql` una vez en esa base descartable
y añadir `COP_PRIVACY_QA_EXPORT=1` al comando de Playwright. Para publicación y
entrega integral, configurar también `PrivacyRequests__FulfillmentEnabled=true`,
una clave de QA separada, habilitar la audiencia `erp` y pasar
`COP_PRIVACY_QA_FULFILLMENT=1`. El fixture asigna ERP al segundo usuario
sintético; Playwright firma el token de QA con el permiso de publicación.
Para verificar borrado acotado, habilitar `PrivacyRequests__DeletionEnabled=true`
y pasar `COP_PRIVACY_QA_DELETION=1`. El fixture también carga una fila de
preferencias para cada titular.

Playwright sustituye los endpoints no relacionados con el ticket. Los endpoints
de solicitudes, firma JWT, aislamiento y persistencia usan Auth y PostgreSQL
reales. No se usan datos de pacientes ni servicios externos.

### Evidencia de la ejecución local (25-11-2026)

- Playwright falló sobre `dev` al buscar la tarjeta de servidor. Luego pasó contra
  el Auth actualizado y PostgreSQL 17 sintético: validación de formatos y
  confirmación, error temporal y reintento, cancelación sin POST, aislamiento
  entre cuentas, cuatro POST concurrentes con un solo comprobante y cuenta
  activa tras solicitar borrado.
- El mismo JWT recibió `200` con el canal habilitado y `404` con la
  configuración por defecto. La regresión de KAN-157 pasó a 320 y 390 px.
- Una copia `pg_dump` de la tabla restauró tres comprobantes sintéticos en
  una base separada, con el mismo conteo y huella del contenido. `Down` y
  `Up` también pasaron en otro clon descartable sin alterar las tres filas
  de origen. Esto no valida backups de producción ni el borrado diferido.
- Build de Auth y app correctos. Suite Auth filtrada: 446 pasaron y 8
  quedaron omitidas por la suite. App: 2.234 pruebas pasaron.
- Con la descarga habilitada, Playwright falló primero porque no encontraba el
  botón. Luego descargó el CSV del primer titular y obtuvo el JSON del segundo
  con PostgreSQL 17 y proyecciones sintéticas de App. Comprobó alcance, valores,
  protección de fórmula en CSV, ausencia de datos de la otra cuenta, denegación
  al usar un comprobante ajeno y registro de ambas preparaciones sin cerrar las
  solicitudes. Build y suites de Auth y app volvieron a pasar.
- Se ensayó el script existente de archivo de `default-83w`: simulación,
  archivo, `pg_dump` y restauración a otra base, y reversión allí. La copia
  conservó el estado archivado, ambas inscripciones sintéticas conservaron
  su contenido y la reversión no afectó la fuente. No cubre otros módulos
  retirados ni restauraciones de producción.
- Entrega revisada: Playwright rechazó operador sin permiso, titular ajeno,
  manifiesto incompleto y sobrescritura de una copia disponible. Los dos
  titulares de QA recibieron CSV y JSON y el cierre se registró al servir el
  expediente. Se verificaron cifrado, caducidad y retirada de ambas cargas
  vencidas sin perder el registro de entrega.
- Preflight: tres pruebas verifican el bloqueo sin aprobación, suspensión,
  plazo y archivo sin restauración. Un ensayo PostgreSQL de restauración
  detectó un registro sintético reaparecido y no aprobó la reapertura hasta
  quitarlo del clon. La fila ajena al trámite quedó intacta.

### Evidencia de verificación y borrado acotado (27-11-2026)

- Playwright falló primero contra el backend anterior porque la operación
  de verificación no existía. Con el nuevo flujo, verificó el plazo de 90
  días, un vencimiento anterior, la posibilidad de acortar sin extender,
  la denegación a un titular y a un token ERP sin permiso, y la fecha
  visible al reabrir el comprobante.
- Se ejecutó el borrado explícito de una fila de preferencias en PostgreSQL
  antes del vencimiento. Dos llamadas concurrentes devolvieron el mismo
  resultado y el trámite quedó abierto. Se mantuvo activa la cuenta, se
  conservó la preferencia del segundo titular y el hash clínico no cambió.
  Una falla inducida al escribir la auditoría revirtió la supresión.
- Una copia PostgreSQL anterior al borrado restauró esa preferencia.
  A partir del asiento posterior al snapshot, el preflight identificó
  `auth.UserPreferences` como pendiente de reconciliar y rechazó reabrir.
  Pasó tras reaplicar la supresión solo en el clon. Las cuatro pruebas
  Python incluyen la separación de fuentes, además de fechas, excepciones
  y registros reaparecidos.
- Builds de Auth y app correctos, 458 pruebas Auth y 2.241 de app aprobadas.
  Ocho pruebas Auth quedaron omitidas por la suite. No se usaron ni
  restauraron datos de producción, no se modificaron los plazos de AWS y
  no se habilitó el flujo de borrado en producción.
