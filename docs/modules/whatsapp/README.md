# Canal WhatsApp

Operación preparada, pendiente de aprobación: [OPERACION.md](OPERACION.md).
Informe y evidencia local: [INFORME.md](INFORME.md).
Vinculación por OTP (fase 10): [INFORME-OTP.md](INFORME-OTP.md).

Módulo del monolito (`CoppAddresd.WhatsApp`), con contratos en Application y entidades
en Domain. No incorpora otro servicio desplegable. OpenSpec:
`openspec/changes/canal-whatsapp` en la raíz del workspace (decisiones D1–D9).

## Configuración

Solo variables de entorno: `WHATSAPP_ENABLED` (`0` por defecto),
`WHATSAPP_ACCESS_TOKEN`, `WHATSAPP_PHONE_NUMBER_ID`, `WHATSAPP_APP_ID`,
`WHATSAPP_APP_SECRET`, `WHATSAPP_WEBHOOK_VERIFY_TOKEN`. Las cinco son obligatorias
al habilitar el módulo. Con el flag apagado no registra servicios del canal.

## Persistencia

### Vinculación por SMS (fase 10)

Para un número desconocido: `VINCULAR` → documento → SMS al teléfono registrado →
código de seis dígitos → confirmación. También se admite el documento directamente.
`REENVIAR` solicita otro código; `CANCELAR` termina. Nunca se pide ni se usa una
contraseña. Hasta aprobar el código no hay vínculo ni llamada al agente; el siguiente
mensaje usa el chat existente. Un número ya resuelto por teléfono conserva su flujo.

`FindActivePatientForBindingQuery` consulta Patients: exactamente un perfil con
status Activo y sin soft delete, por documento (comparación sin distinción de caso).
Documento ausente, inactivo, eliminado o ambiguo recibe la misma guía. Antes y después
de verificar se revalida ese perfil; el sujeto emitido por Auth debe corresponder al
user_id del paciente. No se reasignan vínculos existentes a otro número/paciente.

Cliente HTTP: `WhatsApp:Auth:BaseUrl` (env `WhatsApp__Auth__BaseUrl`), por defecto
`http://localhost:5123`. Configurar el origen/prefijo del Auth accesible desde la API;
el cliente añade `api/auth/id-lookup`, `send-otp` y `verify-otp`. No contiene secretos.
Solo canal PHONE, application app; sin reintentos automáticos, redirects, cookies
compartidas ni logging de HttpClient. Timeout 10 s por petición, CancellationToken.
Tokens/Set-Cookie/devCode nunca salen por WhatsApp ni se guardan en la sesión.

Auth acepta el campo opcional `contactId=phone` en verify-otp y confirma con
`verifiedContactId=phone` solo tras verificar PHONE; un OTP EMAIL pendiente no sirve
para este consumidor. Los clientes que omiten contactId conservan su contrato.
El canal falla cerrado si Auth aún no soporta la confirmación: desplegar Auth con
este cambio antes de habilitar la nueva vinculación. No se ejecutó ese despliegue.
Se siguen respetando OtpSecurity/rate limit/Twilio y el HTTP 429 de Auth, sin fabricar
IP por paciente ni reintentar para eludirlos.

`whatsapp.binding_sessions`: una fila única por wa_id, estado, paciente/contacto,
intentos y las dos reservas SMS más recientes. Expiración absoluta de 10 min desde
el inicio, tres intentos y dos reservas por hora móvil. Reiniciar/cancelar conserva
la cuota; reenviar no reinicia intentos ni expiración. Ante envío fallido/timeout la
reserva se conserva por prudencia: evita superar dos SMS ante aceptación incierta.
Puede agotarse antes por las protecciones de Auth. Tras finalizar/expirar se comienza
de nuevo con VINCULAR. Nunca se guarda documento, código ni tokens en BindingSession.

Lease persistido de dos minutos serializa el flujo. Se reserva el intento/envío
antes de HTTP, sin transacciones abiertas durante Auth. Estado Sending/Verifying
recuperado tras caída termina sin repetir un SMS/verificación a ciegas. La creación
de patient_links y confirmación es atómica, con comprobación de lease y unicidad.
Se devuelve solo teléfono enmascarado; los eventos crudos de vinculación se limpian
al guardar su respuesta (payload `{}`, texto `[vinculación]`). Auth enmascara también
el documento en sus logs OTP. Los códigos pueden expirar antes por el motor Auth;
los 10 minutos son el máximo de la sesión del canal.

Schema exclusivo `whatsapp`, historial `whatsapp.__ef_migrations_history`.
El modo `--migrate` aplica este contexto incluso con el canal apagado; no necesita
credenciales de Meta. Nunca auto-migrar durante el arranque normal.

```powershell
dotnet ef database update --project src/CoppAddresd.WhatsApp --context WhatsAppDbContext
dotnet ef migrations list --project src/CoppAddresd.WhatsApp --context WhatsAppDbContext
dotnet test tests/CoppAddresd.WhatsApp.Tests
```

## Envío

`IWhatsAppMessageSender` (Application) devuelve wamid o error clasificado, sin
propagar cuerpos de Meta. `MetaCloudWhatsAppSender` usa Graph v23.0, Bearer por
petición y timeout de 15 s. HTTP 429/5xx y códigos 1/2/4/80007/130429 son
transitorios; 190 tiene prioridad permanente. No hay retry HTTP automático.
El worker adapta encabezados, énfasis, enlaces, tablas y bloques de código.
Divide el texto resultante en fragmentos de hasta 4096 unidades UTF-16 y
prefiere cortes de párrafo, línea o palabra. Mantiene los grafemas completos
cuando caben en un mensaje. El sender rechaza texto si la última entrada tiene 24 h
o más; los recordatorios usan siempre plantillas deterministas (D9).
HttpClient no registra cuerpos, URLs ni headers del proveedor. Los logs propios
solo contienen teléfono enmascarado y códigos de error numéricos/internos.

Contrato contrastado con [texto de Meta](https://whatsapp.github.io/WhatsApp-Nodejs-SDK/api-reference/messages/text/)
y Orbit (payload de plantillas). La verificación local no envía mensajes reales.

## Webhook

`GET /api/v1/whatsapp/webhook`: handshake subscribe y verify token válido → texto
del challenge, 200; cualquier otro caso → 403. Flag apagado → 404.
`POST`: HMAC-SHA256 sobre bytes originales, comparación de tiempo constante;
401 sin persistencia ante firma ausente/inválida. Máximo 1 MiB por request.
El endpoint persiste cada mensaje antes del 200 (índice único wamid), cuenta
duplicados y emite una señal de cola en proceso. La primera persistencia
actualiza la última interacción y cancela los recordatorios pendientes
programados hasta ese momento en una sola sentencia. El agente se ejecuta en
el worker.
El cuerpo y la persistencia tienen un presupuesto de cuatro segundos. Los fallos
de persistencia y el vencimiento del presupuesto devuelven 429 para admitir
reentrega. Un JSON válido con estructuras desconocidas se ignora.
Solo acepta el phone_number_id configurado. Recibos de estado no abren ventana.
El payload completo es información sensible: no se imprime en logs.

### Recibos de estado (KAN-54)

Cada `statuses[]` aceptado se persiste en `whatsapp.message_receipts` (append-only, único
`(wamid, status)`) **antes** de reenviarse al receptor de SOS; un fallo del receptor ya no pierde el
recibo. El estado local avanza de forma monótona `sent < delivered < read` y `failed` gana, tanto en
`whatsapp.outbound_messages` (cada fragmento del chat, con `wamid`) como en
`reminder_deliveries.receipt_status/at/error_code`. Se toleran duplicados, orden invertido y wamid
desconocido (solo queda el recibo). Si el recibo llega antes del ACK del envío, el INSERT del saliente
concilia después con el recibo más avanzado. Los logs muestran el estado y el wamid enmascarado
(últimos 8 caracteres), nunca el teléfono ni el contenido.

## Identidad y conversación

`FindActivePatientByPhoneQuery` pertenece a Patients; su adaptador consulta solo
identidad y teléfono del perfil activo, sin datos clínicos. Normaliza prefijo,
espacios y puntuación (nacional colombiano sin prefijo → 57). Ante varios perfiles
activos con el mismo número responde como desconocido: no elige arbitrariamente.
La primera búsqueda normalizada recorre perfiles; ocurre una vez por vínculo y
no añade índices ni funciones en schemas ajenos. Luego `patient_links.wa_id`
es la fuente del canal (D3). V1 admite un número por paciente y una conversación.
El vínculo conserva user_id para reutilizar la identidad y consentimiento del chat.
Antes de cada turno del agente se comprueba por ID que el perfil sigue activo y
pertenece al usuario vinculado. Un vínculo sin propietario válido recibe guía
segura. El texto entrante viaja únicamente en el campo `message`.
Un teléfono desconocido recibe la guía de vinculación por OTP cuando el flujo
está habilitado, o instrucciones para continuar en la app ANTARES cuando no está
disponible. En ambos casos se omite el agente, se protege el contenido de
vinculación y no se expone información clínica.
`ON CONFLICT` y unicidad evitan vínculos/hilos duplicados en primeras entradas
concurrentes. No hay FK desde `whatsapp` a `app` ni a `auth`.

## Conversación y recuperación

El worker sondea la BD cada segundo además de drenar señales de cola. Reclama un
evento con `FOR UPDATE SKIP LOCKED` y lease de cinco minutos en una sola sentencia,
libera el lock y procesa con límite de dos minutos. Ordena por recepción por número.
Un turno anterior pendiente impide que otro worker adelante el mismo hilo.
Invoca `ChatCommand` por MediatR (el mismo handler de ChatController) con el agente
`whatsapp`, `channel=whatsapp`, `channel_message_id` igual al `wamid`,
`supports_vita=true`, `user_id` y `patient_id` verificados. El control de
exámenes de la app conserva su política de canal.
El cliente de IA mantiene su validación de consentimiento, también en background.
App, voz Vita y WhatsApp usan `proactive-{userId}` como hilo predeterminado.
El [contrato de sincronización y matriz de KAN-74](KAN-74-PARIDAD.md)
detalla la continuidad, la transición de hilos y los bloqueos de paridad.

Idioma inicial es; cambia a en solo con señal clara y se conserva entre turnos.
SALIR/STOP e INICIAR/START son insensibles a caso y acentos; confirman y cambian
opt_out sin consultar al agente. Opt-out no bloquea conversaciones iniciadas por
el paciente. Los mensajes sin texto reciben instrucciones para continuar por texto.
Si el agente falla, excede su límite de 90 s o devuelve una respuesta vacía tras
aplicar el formato de WhatsApp, se guarda un mensaje seguro en ES/EN y el código
de causa. La preparación del turno tiene un límite de 100 s para reservar tiempo
al envío del respaldo. Los fallos de preparación también producen un respaldo
cuando se puede guardar y enviar. Los logs registran el identificador del evento,
el código y el tipo de excepción cuando aplica, sin cuerpos del proveedor.

La ejecución se marca en la BD antes de llamar a AI. Un resultado perdido
genera una respuesta segura al recuperar el evento. El POST de WhatsApp a AI
se intenta una sola vez para proteger las escrituras de las tools.
La secuencia exacta de fragmentos se persiste antes del envío y cada fragmento
aceptado avanza un cursor durable. Los eventos enviados parcialmente en la
versión anterior se completan con sus cortes originales. Si Meta acepta el
respaldo, el evento se cierra y conserva el código del agente. Un fallo
transitorio de envío guarda su propio código en el evento, registra un
reintento del fragmento pendiente a los 5, 10, 20 o 40 s y
utiliza la respuesta guardada. El rechazo permanente, la ventana cerrada o el
agotamiento de cinco intentos cierran el evento y permiten procesar el siguiente
mensaje del número. Si el quinto intento fue interrumpido, al vencer el lease se
cierra con `attempts_exhausted` sin volver a llamar al agente ni a Meta.

La aceptación de Meta acredita el envío al proveedor. La entrega al dispositivo
requiere confirmación de Meta. Si el proceso cae entre la aceptación del fragmento
y la persistencia de su cursor, al recuperar el evento puede reenviar ese fragmento.

## Acompañamiento (D9 actualizado por Luis)

No hay funciones ni plantillas de citas. El nutricionista responde por el agente
dentro de la conversación y su ventana de 24 h; no tiene plantilla proactiva.

| Tipo               | Fuente canónica en el backend                                                                                        | Motivo resuelto                                                                   |
| ------------------ | -------------------------------------------------------------------------------------------------------------------- | --------------------------------------------------------------------------------- |
| Bienestar          | Paciente activo + inscripción `Active`                                                                               | Interacción, baja o programa inactivo                                             |
| Racha              | `streak_states`: CurrentStreak > 0 y LastActiveDate = ayer en Bogotá                                                 | Día calificado hoy o racha ya no vigente                                          |
| Podcast            | `program_weeks.tasks_snapshot` del día, fallback `weekly_day_templates.media_id`, medio Podcast publicado y no demo  | `task_completions` podcast en la fecha local o contenido desasignado/no publicado |
| Evaluación inicial | Asignaciones pending/in_progress de `bateria-inicial`, activa y AutoAssignOnPatientCreate; sin evaluación completada | Evaluación entregada, asignación cancelada/vencida o batería deshabilitada        |

Racha en riesgo significa racha vigente ayer que aún no calificó hoy, la misma
vigencia que `ProgramRepository.EffectiveCurrentStreak`; no se altera el contador
ni el inventario de congelamientos. Se reutiliza `WeekSnapshotJson.Parse` para el
podcast y la prioridad snapshot → plantilla del snapshot móvil. La evaluación
inicial es elegible aunque todavía no exista inscripción activa; los otros tres
tipos requieren programa activo. No se recuerdan baterías clínicas ajenas al
onboarding. Las claves incluyen inscripción/fecha/medio o asignación real.

`ProgramAccompanimentQuery` lee por lotes de hasta 100 vínculos, sin consultas por
paciente en loops. El scheduler crea los motivos con un INSERT por lote y
ON CONFLICT. Los flujos de asignar batería, asignar podcast a inscripción y
completar tarea invocan hooks opcionales; SubmitEvaluation cancela después del
commit clínico. El polling reconcilia también cambios de plantilla, alta automática
y fallos del hook. Cada envío revalida el motivo contra su fuente actual.

`WhatsApp__Reminders__Enabled=false` por defecto: habilitar solo tras aprobación
de las ocho plantillas en Meta. Configuración no secreta: StartHour=9, EndHour=18
(fin exclusivo), DailyHour=10 y ActiveConversationMinutes=120. Zona America/Bogota,
lunes a viernes. Fuera de franja se difiere al siguiente día hábil. Una aceptación
impone 24 h sin otro toque del mismo tipo; el diferimiento no consume intento.
La [verificación de KAN-74](KAN-74-PARIDAD.md#84-consentimiento-y-meta) encontró
que un vínculo nuevo es elegible sin opt-in explícito. Este punto queda abierto
para el flujo de consentimiento antes de habilitar plantillas.

Estados de Reminder: Pending, Completed, Cancelled, Exhausted. Cada intento tiene
fila ReminderDelivery (paciente, tipo, número, fecha, wamid, estado y error).
Accepted significa aceptación por Meta, no confirmación de lectura/entrega final.
Un lease vencido marca el intento anterior Dispatching como Unknown. Los errores
transitorios esperan 120, 240, 480 y 960 minutos (ajustados a franja), máximo cinco
intentos; los permanentes cancelan. El índice parcial admite un pendiente por tipo
y paciente. El claim usa SKIP LOCKED; reserva intento+entrega en transacción corta,
confirma y después llama a Meta. Los updates finales no reabren cancelaciones.

El webhook hace visible la interacción antes de su ACK y cancela reintentos ya
programados; el procesador cancela todos los pendientes del paciente antes del
agente. Un envío ya aceptado/en curso en Meta no se puede revocar.

## Pruebas locales

El proyecto `tests/CoppAddresd.WhatsApp.Tests` separa traits Category=Unit e
Integration. Integra PostgreSQL real en bases desechables `copp_whatsapp_test_*`;
solo admite host local. Usa COP_TEST_DB_CONNECTION si existe o configuración local
del API sin imprimirla. Las pruebas de recordatorios crean el modelo EF completo
en esa BD temporal y usan los repositorios/handlers clínicos reales. No migran
schemas de otros módulos en la BD de desarrollo. El AI Service se simula por HTTP
con TestServer y el cliente/handler de chat reales. Meta usa fakes controlados.
