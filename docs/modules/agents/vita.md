# Vita: fachada de capacidades del paciente

Base de integración `cbaed6e` (F2 2.11), sin incorporar WIP de Bienestar.
La fachada compone fuentes existentes; AI conserva routing, generación, RAG,
tokens de voz y persistencia del hilo.

## Rutas e identidad

| Método | Ruta | Acceso |
| --- | --- | --- |
| GET | `/api/v1/vita/me/context?scope=day` | JWT `aud=app`, identidad del JWT |
| GET | `/api/v1/vita/me/agents` | Igual; todos los tipos activos con versión publicada activa coincidente |
| GET | `/api/v1/internal/vita/context?userId=<uuid>&scope=day` | `X-Internal-Key` de AI y `Vita:InternalEnabled=true` |
| GET | `/api/v1/internal/vita/agents?userId=<uuid>` | Igual |
| GET | `/api/v1/internal/vita/profile?userId=<uuid>` | Igual, perfil activo y consentimiento de chat |
| GET | `/api/v1/internal/vita/vitals?userId=<uuid>&from=&to=` | Igual, ventana local de hasta 31 días |
| GET | `/api/v1/internal/vita/medications?userId=<uuid>` | Igual y acceso del paciente al módulo de medicamentos |
| GET | `/api/v1/internal/vita/nutrition?userId=<uuid>&date=` | Igual, ciclo publicado e historial acotado |
| GET | `/api/v1/internal/vita/assessments?userId=<uuid>` | Igual, baterías y resultados visibles al paciente |
| GET | `/api/v1/internal/vita/documents?userId=<uuid>` | Igual, documentos propios paginados |
| GET | `/api/v1/internal/vita/documents/{id}?userId=<uuid>` | Igual, enlace de lectura de 120 s |
| POST | `/api/v1/internal/vita/capture/propose?userId=<uuid>` | Clave interna, consentimiento y `Vita:CaptureEnabled=true` |
| POST | `/api/v1/internal/vita/capture/confirm?userId=<uuid>` | Igual, con token vigente y confirmación explícita |
| POST | `/api/v1/vita/me/task-completion-proposals` | JWT app y `Vita:TaskCompletionEnabled=true` |
| POST | `/api/v1/vita/me/task-completion-confirmations` | Igual; confirmación explícita |
| POST | `/api/v1/chat/voice/session` | Ruta existente, JWT y consentimiento de voz |
| DELETE | `/api/v1/chat/voice/session/{sessionId}` | JWT app; permite revocar tras retirar consentimiento |
| POST | `/api/v1/internal/vita/voice/v1/chat/completions` | Clave de voz dedicada y `Vita:VoiceProxyEnabled=true` |
| POST | `/api/v1/vita/attachments` | JWT app, paciente activo y consentimiento de chat |
| POST | `/api/v1/vita/attachments/{attachmentId}/archive` | Igual, con confirmación explícita |
| DELETE | `/api/v1/vita/attachments/{attachmentId}` | JWT app y paciente activo; admite revocación del consentimiento |

Scopes de `context`: `day`, `nutrition`, `assessments`, `podcast`, `exercise`,
`progress`. Desconocido → 400. El scope de contexto no acepta selectores de
fecha, inscripción o paciente. En las rutas internas `userId` lo inyecta el runtime autenticado desde su
contexto; no es argumento de herramienta. La clave interna confía en ese runtime:
no permite exponer la fachada directamente a proveedores o al móvil.

Cada consulta resuelve paciente activo/no eliminado y verifica consentimiento
`chat`. Crear voz exige además consentimiento `voice`. No basta conocer un ID.
La revocación de voz se reenvía con el dueño del JWT incluso si ya retiró el
consentimiento. Todas las respuestas de estas rutas se sirven sin caché HTTP.

## Adjuntos efímeros y archivo en expediente

### Subir un adjunto

`POST /api/v1/vita/attachments` recibe `multipart/form-data` con un único `file`
obligatorio, `threadId` opcional de hasta 128 caracteres y `language` opcional
(`es` o `en`). La API admite hasta 10 MiB de archivo y 64 KiB adicionales de
envoltura. El AI aplica sus propios límites por tipo, páginas y validez del binario
para JPG, PNG, WEBP y PDF. Si falta `threadId`, el AI registra el adjunto sin
vínculo de hilo. El AI recibe un nombre genérico con la extensión y las identidades
resueltas a partir del JWT.

`201 Created`:

```json
{
  "attachmentId": "09474655-5eb4-49f8-9290-9bd4ad171df5",
  "kind": "pdf",
  "contentType": "application/pdf",
  "sizeBytes": 123456,
  "pageCount": 3,
  "expiresAt": "2026-11-19T12:00:00Z"
}
```

`kind` es `pdf` o `image`. `pageCount` es un entero o `null`. El vencimiento
lo fija AI. La subida deja el archivo en el almacén efímero de AI.

### Usar adjuntos en un turno

`POST /api/v1/chat` admite el campo opcional `attachmentIds: string[]`. Cada
elemento es un UUID con guiones y la lista tiene hasta cuatro elementos. Una
lista ausente, nula o vacía conserva el contrato de chat previo. Una lista con
elementos exige JWT app, paciente activo, consentimiento de chat y
`supportsVita: true`.

```json
{
  "message": "Resume este documento",
  "threadId": "proactive-69685dcd-36dc-4320-a806-56c4a365083f",
  "supportsVita": true,
  "attachmentIds": ["09474655-5eb4-49f8-9290-9bd4ad171df5"]
}
```

El backend reenvía la lista como `attachment_ids` y envía `user_id` y
`patient_id` de la sesión. Aplica la resolución de hilo existente. AI comprueba
propiedad, vigencia y compatibilidad con el hilo. El `200 OK` agrega
`attachments: [{ "attachmentId": "<uuid>", "kind": "pdf", "readable": true }]`
cuando AI devuelve el campo para un turno con adjuntos. `readable` es booleano.
El AI de referencia impone un máximo de tres. Si rechaza la lista de cuatro,
la API devuelve `422 attachment_limit_exceeded`.

### Guardar en el expediente

`POST /api/v1/vita/attachments/{attachmentId}/archive` recibe JSON.

```json
{
  "confirmed": true,
  "title": "Informe de seguimiento",
  "documentTypeId": "3a2a43fb-692f-4d2e-a28c-3976d486b917"
}
```

La primera confirmación requiere un título de 1 a 200 caracteres tras quitar
espacios y un tipo de documento activo que admita la extensión. La API comprueba
el tamaño exacto y SHA-256 antes de escribir en el storage privado configurado
(S3 en producción). Persiste un documento raíz `Ready` bajo
`documents/{patientId}/vita-attachments/{attachmentId}.{ext}`, con la clínica
del perfil. La respuesta es `201 Created` al insertarlo y `200 OK` al repetir
la confirmación. Ambos devuelven el mismo esquema.

```json
{
  "documentId": "09474655-5eb4-49f8-9290-9bd4ad171df5",
  "attachmentId": "09474655-5eb4-49f8-9290-9bd4ad171df5",
  "title": "Informe de seguimiento",
  "documentTypeId": "3a2a43fb-692f-4d2e-a28c-3976d486b917",
  "contentType": "application/pdf",
  "sizeBytes": 123456
}
```

`documentId` coincide con `attachmentId`. La PK de `app.documents` evita
duplicados entre réplicas y el trigger existente agrega al outbox en la misma
escritura. Un reintento confirmado devuelve el documento existente incluso
después de caducar el adjunto. Si se eliminó el documento, devuelve `404`.
El expediente lo sirve mediante las rutas de documentos autorizadas.

`DELETE /api/v1/vita/attachments/{attachmentId}` devuelve `204 No Content`
tanto para un adjunto eliminado como para uno ausente, ajeno o caducado. La baja
afecta al efímero. El documento del expediente tiene su propio ciclo de vida.

### Errores de adjuntos

El cuerpo de los errores específicos es `{"code":"<código>"}`.

| HTTP | Código | Causa |
| --- | --- | --- |
| 400 | `attachment_confirmation_required` | Falta `confirmed: true` |
| 400 | `invalid_attachment_archive_request` | Título o tipo ausente o inválido |
| 400 | `attachment_limit_exceeded` | Más de cuatro adjuntos en el chat |
| 400 | `invalid_attachment_request` | ID de adjunto del chat inválido |
| 401/403 | `vita_identity_required` | Identidad app requerida |
| 403 | `vita_attachments_disabled`, `attachment_forbidden` | Flag de AI apagado o rechazo de acceso por AI |
| 404 | `vita_attachment_not_found` | Adjunto inexistente, ajeno, vencido o de otro hilo en chat. También se usa si se eliminó el documento archivado |
| 409 | `attachment_archive_conflict` | Colisión con un documento distinto |
| 413 | `attachment_too_large` | Tamaño excedido |
| 415 | `attachment_type_not_allowed` | Tipo o firma no admitidos |
| 422 | `attachment_empty`, `attachment_mime_mismatch`, `attachment_too_many_pages`, `attachment_pdf_encrypted`, `attachment_unreadable`, `invalid_attachment_request` | Archivo o formulario rechazado |
| 422 | `attachment_document_type_invalid`, `attachment_document_type_mismatch` | Tipo de expediente inactivo, inexistente o incompatible |
| 422 | `attachment_limit_exceeded` | El AI limita la cantidad por turno |
| 502 | `attachment_integrity_mismatch`, `attachment_invalid_response` | Binario que no coincide con metadatos, o respuesta de AI inválida |
| 503 | `attachment_service_unavailable`, `attachment_store_unavailable` | Canal interno de AI o su almacén indisponibles |

Las respuestas de autenticación, consentimiento (`{code, scope}`), perfil no
disponible (`ProblemDetails` 403 con `detail: "VITA_PATIENT_UNAVAILABLE"`),
JSON mal formado y errores inesperados siguen los formatos comunes de la API.

### Operación

La subida y el uso en chat dependen de `VITA_ATTACHMENTS_ENABLED` en AI. El
worker del backend consume el outbox con `Vita:DocumentIndexingEnabled=true`.
Indexa PDFs con texto y conserva la política del lector existente para imágenes
y PDFs escaneados sin texto. El archivo confirmado no crea mediciones.

S3 recibe el objeto verificado antes del INSERT. Si la escritura en PostgreSQL
falla o se cancela después del PUT, puede quedar un objeto privado sin fila.
Mientras el efímero siga vigente, una nueva confirmación reutiliza la misma
clave. Las réplicas pueden escribir el mismo binario al mismo tiempo, con una
sola fila y un solo trabajo de indexación. El backend procesa hasta 10 MiB
en memoria por descarga de archivo confirmada.

## Lecturas clínicas internas de Ola A

`profile`, `vitals` y `medications` devuelven DTOs directos. El contrato enumera
sus campos, unidades, fechas, estados y errores. [CONTRATOS-A.md](../../../openspec/changes/vita-omnicanal-2026-11/CONTRATOS-A.md)

`profile` proyecta el perfil mínimo, alergias, metas y restricciones registradas del
plan vigente y la serie de peso, talla e IMC. `vitals` devuelve el último punto por
métrica y el historial acotado, con unidades y origen de cada lectura.
`medications` aplica la disponibilidad del módulo y del paciente. Proyecta las
tomas del día local con el mismo cálculo de horarios y estados del módulo.

`nutrition` compone hasta 30 días de la prescripción publicada, con recomendaciones
publicadas y el historial de comidas. `assessments` usa el resultado persistido
y la proyección móvil. Omite resultados en baterías pendientes, respuestas y
calificadores de subescalas. `documents` solo lista metadatos propios y firma
la URL después de validar la pertenencia y el estado del documento.

Las lecturas envían la fecha del paciente y los instantes en UTC según el DTO.
Las lecturas incluyen el identificador canónico de una medición o toma para que
el runtime pueda proponer una captura sobre el recurso correcto.

## Captura confirmada

Las rutas de captura están apagadas por defecto. Al habilitarlas, todas las
réplicas requieren el mismo `Vita__CaptureProposalSecret` de 32 bytes aleatorios
en Base64. Debe ser distinto de las claves de JWT, AI, voz y propuestas de tareas.
El token AES-GCM usa el propósito `CoppAddresd.Vita.Capture.v1` y vence a los
cinco minutos. Vincula la identidad, el canal, el turno de la captura y los datos
validados. La confirmación llega por una petición posterior con la afirmación
explícita del paciente. El runtime conserva el identificador de la captura
original cuando la conversación avanza.

La propuesta solo lee. La confirmación inserta la medición o registra una
toma/omisión con el módulo de medicamentos y guarda el recibo en la misma
transacción. Un replay devuelve los identificadores originales. El backend
rechaza una clave de turno reutilizada con datos diferentes. Las correcciones
de mediciones guardan una fila nueva y mantienen la original. [Contrato de API](../../../openspec/changes/vita-omnicanal-2026-11/CONTRATOS-A.md)

Comidas y check-in completan el registro nutricional o la tarea emocional de la
inscripción activa. El token fija esa inscripción y el día local, derivados en
el backend. Después del commit el flujo publica los eventos de XP y de tarea
que ya consume el procesador del programa. Las repeticiones devuelven el recibo
sin volver a emitir esos eventos.

## Envelope y fuentes

```json
{
  "status": "ok",
  "asOf": "2026-11-14T14:00:00Z",
  "localDate": "2026-11-14",
  "resourceVersion": "sha256-del-contenido-visible",
  "data": {},
  "actions": [],
  "sources": [{"capability":"podcast","caseUse":"GetSnapshotQuery"}]
}
```

`ok` y `empty` → 200; `forbidden` → 403; `unavailable` → 503; `stale` → 409.
`resourceVersion` opcional en query exige la misma revisión visible. Cambios o
retirada del recurso generan `stale` sin datos/acciones anteriores. No es un
ETag de toda la BD. `asOf` indica composición de la lectura, no revisión clínica.
Ausencia correcta nunca se utiliza como fallback de un timeout. Cancelación
del request se propaga hasta repositorios/HTTP. Una zona horaria inválida se
trata como fuente no disponible, sin asumir UTC silenciosamente.

| Scope | Fuente y proyección |
| --- | --- |
| day | Snapshot: semana vigente, descanso, códigos/títulos/estado/referencia de tareas |
| nutrition | `GetMyNutritionPlanQueryHandler.ReadForPatientAsync`: publicación efectiva, revisión, ventana, día/menú/comidas e indicaciones para paciente |
| assessments | Asignaciones propias visibles, versión, nombre/código, estado y fechas; máximo 100, `hasMore` explícito |
| podcast | Podcast efectivo del snapshot: ID, título, descripción, autor, duración, capítulos y puntos clave |
| exercise | Snapshot efectivo e histórico de sesión ya completada; descanso explícito y parámetros aprobados sin URLs privadas |
| progress | XP/racha/calendario factual y resumen wearable de la semana ISO local |

No se publican documento, contactos, diagnóstico, notas ERP, respuestas de tests,
storage keys, prompts, herramientas, KB ni JWT. El podcast/exercise se abre por
ID en su pantalla/reproductor autorizado existente. La ausencia de semana activa
no activa/materializa una semana futura; día/podcast/ejercicio devuelven `empty`.
La consulta de tests evita el controller que autoasigna baterías al leer.

Si no hay menú vigente, `nutrition` devuelve `empty` y
`data.availability` desde el workflow existente (`pending_review`, `generating`,
`assessments_pending`, etc.). Nunca incluye el borrador. Las capacidades F3 de
reviews/highlights todavía no existen en esta base y no se simulan.

Límite verificado de `GetVitaContextQuery` (2026-11-14): no consulta ni entrega
protocolos clínicos/asignaciones de protocolos o nutracéuticos. `nutrition`
resuelve la prescripción publicada o asignación de plan vigente mediante el caso
de uso existente, pero proyecta únicamente el menú del día seleccionado: ID,
nombre, estado/revisión, ventana, día actual, disclaimer, metas diarias de
calorías/macros/fibra, número de día, agua y comidas con indicaciones para paciente.
No entrega todos los días del plan ni un protocolo completo. `day` solo resume
tareas de hoy; `assessments` son asignaciones de tests, no protocolos. Esta
revisión no amplía el contrato ni incorpora dependencias de Bienestar.

Progreso wearable usa una consulta existente indexada por paciente/rango UTC,
desde el lunes local hasta la lectura. Expone `fromLocalDate`, `toLocalDate`,
`fromUtc`, `timezone`, `aggregation=latest-per-metric-local-day`,
`deviceAvailability` y `deviceMetrics[]` con código, valor, unidad, `observedAt`,
`source=device` y fecha local. Códigos `step_count`, `sleep_minutes`,
`activity_kcal`: este último es gasto de actividad, no ingesta nutricional.
Selecciona la última observación por código/día local, sin sumar snapshots
acumulados. Funciona sin inscripción (`program=null`). Sin registros, lista vacía
y disponibilidad `empty`; no equivale a cero ni acredita mejoría clínica.

## Catálogo ERP

El selector de asignación reutiliza `GET /api/v1/patients?page=1&pageSize=10&search=...`:
`data[].id` es `patient_profiles.Id`; el nuevo `data[].userId` es UUID nullable de
`PatientProfile.UserId` (`auth.users.Id`). El resto del envelope
`data/total/page/pageSize/totalPages` y sus campos existentes conserva su contrato.
Solo cambia `PatientListItemDto` (parámetro opcional final `Guid? UserId = null`),
no el DTO de detalle. Se conservan `Patients.View/ViewOwn` y filtros de clínica/
profesional existentes; no hay búsquedas adicionales por correo ni consultas por fila.
Si `userId=null`, ERP debe bloquear asignación y explicar que no hay cuenta vinculada.

`POST /api/v1/agents/instances` exige ahora la política real `Agents.Update`
(código ya seedeado en Auth, registrado también en el policy provider de la API).
`Agents.View` no autoriza la escritura. Body:
`{ "userId":"<cuenta vinculada>", "agentTypeId":"<tipo>", "metadata":"{}" }`.
`status` y `metadata` siguen siendo opcionales; la creación existente queda Activo.
IDs vacíos → 400; sin autenticación → 401; sin Agents.Update → 403; éxito → 201 con
`id/userId/agentTypeId/agentTypeName/status/metadata/createdAt/updatedAt`.
Se conservan reglas de tipo activo, duplicado user+tipo y FK a Auth; no existe permiso
`Agents.Assign`. No sustituir el userId ausente por patientId, correo o nombre.

## Catálogo Vita: política autorizada por Luis (14-11-2026)

Autorización directa de Luis en esta tarea: «TODOS los agentes publicados y
activos deben estar disponibles automaticamente para TODOS los pacientes activos
con consentimiento». Por tanto, `IVitaAgentCatalogReader` **no exige
AgentInstance ni asignación individual** para Vita. El diagnóstico de cero
instancias de la cuenta lo aportó root; este carril no consultó esa cuenta ni
datos personales para implementar/probar la política.

Una consulta por conjuntos filtra tipo `Status=Activo` y versión publicada activa
coincidente: `ActiveVersionId` no nulo, versión referenciada `IsActive=true`,
`ActiveVersion.Id=ActiveVersionId` y `ActiveVersion.AgentTypeId=AgentType.Id`.
El modelo actual no tiene otro campo `Published`: la publicación visible se
representa con esos estados y el puntero a versión activa. Un borrador, tipo
inactivo, tipo sin versión, versión no activa o puntero a otro tipo queda fuera.
La desactivación se aplica en la siguiente lectura, sin caché de catálogo.

La consulta proyecta `id/runtimeKey` GUID, `slug`, nombre,
descripción/especialidad, `versionId/versionNumber` y `routing` con
`topics/keywords/channels`. El GUID es autoridad; el slug no es único. Un
especialista publicado y activo aparece automáticamente, aunque el paciente no
tenga instancias. `VitaPatientAccess` sigue validando identidad propia, paciente
activo/no eliminado y consentimiento `chat` antes de consultar el catálogo.
El parámetro `userId` del lector se conserva por compatibilidad, pero el catálogo
común no depende de asignaciones del usuario.

Routing procede solo de `AgentType.Metadata.routing`; arrays acotados a 32
strings de 80 caracteres. Canales permitidos `text/voice`; ausencia conserva
ambos y lista explícita vacía no concede canales. Metadata nunca concede
permisos. La configuración efectiva permanece en el runtime sincronizado por
ID/versión. El catálogo común no carga documentos/configuraciones, prompts, KB,
secretos ni overrides en el DTO. La consulta conserva `AsNoTracking`, orden
estable nombre/ID y `ToListAsync(ct)`; desaparece el EXISTS sobre instancias,
sin índices nuevos, N+1 ni migraciones. No se modifican resolución/asignaciones
de ERP, otros canales, autorización de tools ni el alcance de datos del paciente.

Verificación de la política autorizada (14-11-2026):

| Comando / alcance | Resultado |
| --- | --- |
| Unitarias con filtro `FullyQualifiedName~VitaCatalogTests\|FullyQualifiedName~VitaApiHttpTests\|FullyQualifiedName~VitaContextTests` | 40 correctas, 0 fallos, 0 omitidas |
| `dotnet test tests/CoppAddresd.UnitTests --no-build --no-restore` | 2615 correctas, 0 fallos, 105 omitidas existentes |
| Integración PostgreSQL con filtro `FullyQualifiedName~Vita_` | 7 correctas, 0 fallos, 0 omitidas |
| `dotnet build --no-restore --nologo` | 0 errores, 3 advertencias en pruebas existentes de WhatsApp/Telemedicine |
| `git diff --check` | Sin errores |

PostgreSQL 18.6 temporal propio, solo loopback y `data_directory` verificado;
fixture crea BD únicas `copp_nutrition_test_*` con datos sintéticos. Primer
intento: 7 fallos de inicialización por ausencia del rol `app_user` requerido por
migraciones existentes. Tras crear ese rol NOLOGIN solo en el cluster temporal,
las 7 pasaron. Se limpiaron también las 7 BD que dejó la inicialización fallida
y se detuvo el cluster propio. No se consultó ni migró BD compartida/producción.
No se midió rendimiento con volumen representativo ni compatibilidad específica
con PostgreSQL 16. Las regresiones reales comprueban dos pacientes sin instancias,
ambos especialistas visibles, revocación de estado/versión, puntero a versión
ajena y progreso wearable propio; las unitarias conservan rechazo de paciente
inactivo/eliminado/ajeno, consentimiento retirado y cancelación.

## Navegación y confirmación

Acción: `type=navigate`, destino cerrado
`program|nutrition|assessments|podcast|exercise|progress`, `resourceId`,
`localDate`, `resourceVersion`. APP comprueba soporte/flags y puede revalidar
mediante la misma consulta con `resourceVersion` antes de abrir. Los endpoints
de contenido conservan su autorización propia. Abrir/reproducir no completa.

La preparación admite solo `podcast` o `ejercicio`, con fecha local de hoy y
revisión obtenida de su scope (`podcast` o `exercise`):

```json
{"taskCode":"podcast","localDate":"2026-11-14","resourceVersion":"<64 caracteres>"}
```

Devuelve `proposalToken`, tarea, fecha/revisión, `expiresAt` y
`requiresConfirmation=true`. No hay escritura. Confirmar:

```json
{"proposalToken":"<token recibido>","confirmed":true}
```

Token cifrado y autenticado con AES-256-GCM, propósito asociado fijo
`CoppAddresd.Vita.TaskCompletion.v1`, nonce aleatorio por propuesta y etiqueta
de autenticación de 128 bits. Vence a los cinco minutos y vincula
usuario/inscripción/tarea/fecha/revisión. No es el token de voz.

Activar confirmaciones exige `Vita:TaskProposalSecret` (variable
`Vita__TaskProposalSecret`), Base64 de 32 bytes aleatorios, idéntico en todas las
réplicas y conservado en redeploy. Debe venir del gestor de secretos existente;
no se crea infraestructura. La validación al arrancar falla si la feature está
activa sin esa clave o si reutiliza el secreto JWT/AI/voz. El protector también
falla cerrado al resolverlo sin clave. No hay keyring ni claves efímeras
implícitas. Con feature apagada, no se requiere aprovisionar el secreto.
Rotar la clave invalida propuestas pendientes: volver a leer y preparar, sin
reintentar escrituras a ciegas. No guardar tokens ni claves en logs, documentos
o configuración versionada. Dos instancias con la misma configuración pueden
descifrar la propuesta; otra clave o token manipulado se rechaza.

`ProgramRepository.Vita.cs` es una extensión nueva aislada: llama al núcleo
canónico `CompleteTaskCoreAsync` sin modificar `ProgramRepository.cs`, sus reglas
ni DTO compartidos. Es necesaria para que revisión y completación ocurran en
la misma transacción serializable. Usa el lock de inscripción existente y el
advisory lock del usuario que serializa retirada de consentimiento, revalida
propiedad/consentimiento/fecha/recurso y conserva la clave `vita:<proposalId>`.
La estrategia Npgsql existente reintenta serialización con datos releídos.
Duplicados conservan una completación y una concesión de XP. Una propuesta
expirada, ajena, retirada o cambiada se rechaza; descanso no es ejercicio.

Eventos `TaskCompletedMetricEvent`/`XpAwardedMetricEvent` y reconciliación
WhatsApp se emiten tras el commit. No hay nuevo KPI/rollup: se reutiliza el
pipeline CQRS del programa. Signos vitales, emocional, nutrición, nutracéuticos,
respuestas de tests y cambios de plan quedan fuera de estas escrituras.

## Voz, historial y compatibilidad

Texto: `POST /api/v1/chat` y `/api/v1/chat/stream` admiten `supportsVita:boolean`
opcional default false. DTO y commands conservan firmas mediante parámetro opcional
final; upstream envía `supports_vita`. AI solo usa runtime nuevo con enabled+true.
False/ausente mantiene legacy, playground ERP y consumidores internos/WhatsApp.
El opt-in declara compatibilidad, no concede permisos: usuario desde JWT y catálogo/
agente autorizado siguen siendo autoridad. No se aceptan userId/customerId del body
como identidad ni se activa configuración remota desde este campo.

Crear sesión preserva `signedUrl/agentId/conversationId` y añade opcionales
`threadId/sessionId/sessionToken/expiresAt/runtimeMode`. BACK resuelve el perfil
y no acepta `patientId` del cliente. AI firma/verifica el token reducido y
autoriza el hilo en namespace `user_id::thread_id`. BACK no mantiene otra firma
ni tabla de conversaciones. AI es único escritor de turnos generados; no hay
endpoint para importar una respuesta de bot arbitraria desde APP.

Request de sesión: `supportsVitaVoice` boolean opcional, default `false`, propagado
como `supports_vita_voice` al AI. Solo AI decide motor Vita cuando el cliente declara
`true` y el modo está habilitado; clientes antiguos conservan legacy. Un cliente
compatible puede recibir `runtimeMode=legacy` con `sessionId/threadId` durables para
que AI importe al cerrar exclusivamente el transcript verificado del proveedor.
BACK conserva esos campos y DELETE revoca; no acepta respuestas de bot del cliente.
Esta ampliación no activa flags ni modifica configuración remota.

El proxy Custom LLM acepta exclusivamente la clave dedicada configurada en
`Vita:VoiceServiceKey` (mínimo 32 caracteres), independiente de JWT/clave interna.
Reenvía exactamente `X-Vita-Session` (1–4096 caracteres opacos),
`X-Vita-Conversation` (1–200 alfanuméricos/guion/underscore) y
`X-Vita-Turn` (entero 0–1000000), todos de valor único. Otras cabeceras del
cliente no cruzan. El cuerpo JSON conserva su significado y AI valida sesión,
conversación y contenido permitido. `X-Vita-Turn` transporta `system__agent_turns`
del proveedor, que puede empezar en 0: es metadata del contador del agente, no
un ordinal de entradas del usuario ni una identidad. Se reenvía sin incrementar
ni reinterpretar; negativos, inválidos, overflow y cabeceras ausentes siguen
rechazados. SSE se transmite con flush por
chunk, límite total 90 segundos y cancelación, sin reintentos de generación.
Errores upstream se reducen a códigos públicos sin sus detalles.

El cuerpo JSON se deserializa completo como `JsonElement` antes de reenviarse,
preservando campos de OpenAI como `tools`, `tool_choice`, `tool_calls`, contenido
null y mensajes `role=tool` con `tool_call_id`. El límite de petición es **1 MiB**:
64 KiB podía rechazar historial + schemas de herramientas. Sigue siendo acotado;
no es streaming del cuerpo de entrada. La respuesta SSE sí se transmite por
chunks sin esperar al cierre, incluidos deltas de function calls y `[DONE]`.
`language_detection` se ejecuta en el proveedor/AI, nunca en BACK. Formato de
referencia: [Custom LLM y system tools de ElevenLabs](https://elevenlabs.io/docs/eleven-agents/customization/llm/custom-llm#system-tools-integration).

Configuración declarativa preparada: `.github/workflows/deploy-backend.yml`
añade `Vita__VoiceProxyEnabled=true` exclusivamente a `extra_env` de `api`, junto
a los flags Vita existentes. Es el workflow de la rama `dev` que apunta al
cluster ECS existente `cooppadresd-cluster` y sirve `erp.coppadresd.com`; no crea
un entorno separado. La clave dedicada continúa como referencia `valueFrom`
en Secrets Manager y debe coincidir con AI. Localmente el default sigue apagado.
Este cambio de Git no activa el runtime AI ni configura el agente del proveedor:
root valida transporte y activación antes de publicar/desplegar.

Verificación local del transporte/configuración: fixture JSON con tools superior
a 96 KiB y roundtrip `language_detection` → `role=tool`, SSE recibido antes de
EOF, Bearer y tres cabeceras de contexto, cancelación, timeout del cliente 90 s,
flag exclusivo de API y clave como secret. TestServer no aplica el límite de
Kestrel: el test verifica además su metadata de 1 MiB. No se ha medido el plazo
real de 90 s ni se ha probado el límite a través del ALB/proveedor; root conserva
esa validación integral. Pruebas seleccionadas de proxy/config y contexto:
37 correctas, 0 fallos, 0 omitidas; sin llamadas a proveedores ni datos de pacientes.

Comandos de esta revisión:

```powershell
dotnet test tests/CoppAddresd.UnitTests --no-restore --filter "FullyQualifiedName~VitaVoiceProxyHttpTests|FullyQualifiedName~VitaContextTests" --logger "console;verbosity=minimal"
dotnet build --no-restore --nologo
git diff --check
```

Build de solución: 0 errores, 12 advertencias en pruebas existentes ajenas al
cambio. `git diff --check` sin errores. No hubo push ni despliegue desde esta tarea.

Configuración ElevenLabs demostrada por el POC del coordinador: URL **base**
`https://<backend>/api/v1/internal/vita/voice/v1`; el proveedor añade
`/chat/completions`. Auth mediante `custom_llm.api_key.secret_id`, nunca un
Authorization literal en `request_headers`. La app aporta
`secret__vita_session_token`; cabeceras dinámicas usan las variables reservadas
de conversación/turno. El POC con fixtures acreditó transporte y SSE, no audio
móvil, interrupción o integración clínica en producción. Modo nuevo apagado
hasta completar configuración y prueba integral.

Historial conserva mensajes por identidad: `role/text/messageId/channel/status/revision`
(defaults legado null/text/final/1), paginación y orden. Sync conserva
`actions/sources/agent/versionId`; acepta alias upstream `data_sources` si
`sources` no llega. El SSE de chat sigue siendo passthrough: `done` preserva
`thread_id/execution_id/version_id/agent/actions/sources`. APP conserva esa
compatibilidad snake_case en SSE y camelCase en JSON sync/historial.

El GET de historial acepta `syncVoice=false` opcional. Solo con opt-in, JWT de app,
paciente activo/vinculado y consentimientos actuales chat+voz se solicita internamente
`sync_voice=true`. También se revalidan al salir por HTTP: este GET con efectos de
reconciliación no hereda el bypass de lecturas internas normales. Sin permiso de voz
o paciente válido se conserva la lectura de chat sin sync, no se bloquea el texto.
Propiedad: AI recibe siempre el userId del JWT, usa su namespace user::thread y sus
vínculos durables; no se acepta conversationId ni texto de bot del cliente.

Para registrar también hilos que solo se abren, el GET interno añade
`supports_vita=true` derivado exclusivamente del JWT `aud=app`, tanto en lectura
normal como con sync autorizado. La lectura normal y sus fallbacks conservan
esa señal aunque se haya retirado el consentimiento de voz; no autorizan voz
por ella. ERP y llamadas antiguas al cliente no añaden la señal. Ni query/body
del cliente pueden activarla o desactivarla. El registro durable/retención
pertenece a AI; no se modifica el permiso para borrar tras retirar consentimiento.

La respuesta preserva `voiceSyncStatus` nullable (`synchronized|processing|unavailable|busy`)
y por mensaje `source/voiceSessionId/providerMessageId/sourceEventId` opcionales,
además de IDs/estado/revisión. AI es único escritor del transcript verificado. La APP
puede reintentar processing/busy de forma acotada; BACK no añade otro importador/LLM
ni activa el flag AI de sincronización. Errores del sync conservan el chat disponible.

Cada mensaje conserva también `executionId` opcional (`execution_id` de AI), para
feedback desde el snapshot canónico. AI lo obtiene del ExecutionTracker real del
mensaje de texto; legado/voz sin correlación conservan null. BACK lo proyecta
directamente, sin inferir por texto/orden ni consultar una ejecución por mensaje.

### Verificar que un stream terminó en Agentic Wellness (KAN-185)

En ECS → `agenticwellness-api` → Logs, filtrar por `VitaVoiceProxyOutcome`.
El proxy emite un evento por solicitud que llegó al controlador autenticado,
con ID de petición del servidor, estado HTTP, duración y resultado. El evento
no añade credenciales, cabeceras de sesión ni texto de la conversación al log.

- `completed` requiere que el proxy haya escrito y enviado el evento terminal
  SSE `data: [DONE]` y llegado a EOF sin error ni cancelación. No prueba por sí
  solo que ElevenLabs reprodujo el audio.
- `incomplete` significa que el stream cerró sin el evento terminal completo,
  aunque el estado enviado haya sido 200. `client_aborted`, `timeout`,
  `transport_error`, `upstream_rejected`, `invalid_content_type` e
  `invalid_context` tampoco acreditan finalización.

Para QA, registrar la hora del turno y su zona, la versión efectiva en
ElevenLabs y si hubo respuesta audible después del saludo, en sesiones nuevas
separadas para ES y EN. Cotejar los dos turnos con sus respectivos eventos
`completed` y comprobar que no hubo `503 paused`. `RequestPipelineEnd` del
cliente HTTP interno con 200 solo confirma cabeceras recibidas.

### Limpieza del historial (ampliación autorizada)

`DELETE /api/v1/threads/{threadId}/messages` permite al propietario del JWT
(`aud=app`) vaciar la conversación sin exigir consentimientos de lectura. Proxy
interno: `DELETE /api/v1/threads/{thread_id}/state?user_id=…` con `X-Internal-Key`;
AI sigue siendo la autoridad de checkpoints, proyecciones y vínculos de voz.
Solo una confirmación AI válida produce 200 `{threadId, historyRevision,
clearedAt, resetReason}`; fallos nunca generan éxito local. GET añade esos
metadatos conservando paginación y campos existentes, también con historial
vacío. AI legado usa revisión 0 y fecha/motivo null.

El mes se corta en AI el primer día a las 00:00 de America/Bogota, únicamente
para Vita opt-in de app. BACK no programa barridos ni modifica registros
clínicos. Alcance, errores, compatibilidad, cancelación y límites de las pruebas:
[contrato del módulo de chat](../chat/README.md#limpieza-del-historial-vita-manual-y-mensual).

## Verificación y límites

Tests de unidad y pipeline HTTP con AI simulado cubren auth, claves, cabeceras,
SSE, cancelación, mapeos aditivos, ausencia/pendiente/descanso/stale y propiedad.
Tests PostgreSQL usan BD únicas descartables del fixture: lecturas sin mutación,
catálogo común publicado sin instancias, confirmación concurrente, stale/cuenta ajena, rollback por
cancelación y wearable propio. Ninguna migración se aplica a la BD compartida.

Validación final local: build de solución 0 errores; suite unitaria completa
2559 correctas, 105 omitidas, 0 fallos. Siete casos PostgreSQL reales acreditados
en ejecuciones separadas; los cinco del flujo más la lectura sin mutación y el
wearable. Servidor temporal PostgreSQL 18.6; no acredita compatibilidad medida
en PostgreSQL 16 ni percentiles de producción.

Para root/AI: [script de prueba aislada](../../../scripts/vita/Invoke-VitaAiPostgresTests.ps1)
verifica que el servidor local sea el cluster temporal Vita, crea una BD única,
configura el opt-in sin imprimir URI, ejecuta el test AI de recibos/revocación
con dos conexiones y elimina exclusivamente esa BD. `-ProvisioningOnly`
comprueba aprovisionamiento/limpieza sin ejecutar AI (verificado). No migra ni
modifica schemas compartidos. La instancia temporal permanece disponible para
la integración coordinada; si desaparece el marcador, falla cerrado.

No se midieron percentiles con volumen representativo ni se acreditó audio de
dispositivo. Los resultados y comandos finales se registran en el handoff.
La CLI Postman instalada no admite `postman status`; la evidencia ejecutable
se mantiene en los tests .NET y el contrato compartido, sin publicar workspace.
