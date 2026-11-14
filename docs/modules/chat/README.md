# Módulo de Chat / Integración AI

Integración con el AI Service (Python) vía HTTP, con soporte para chat síncrono y streaming SSE.

La fachada de capacidades Vita, el proxy de voz y los campos aditivos del
historial se documentan en [Vita](../agents/vita.md). AI conserva la persistencia
única del hilo; BACK preserva IDs/canal/estado/revisión y las acciones/fuentes.

## Arquitectura

```
┌─────────────────────────────────────────────────────────────┐
│                    API (Puerto 5122)                          │
├─────────────────────────────────────────────────────────────┤
│  Controllers                                                │
│  ├── ChatController [Authorize]                             │
│  │   ├── POST /api/v1/chat          → Chat síncrono         │
│  │   ├── POST /api/v1/chat/stream   → Streaming SSE         │
│  │   └── POST /api/v1/chat/feedback → Feedback (Fase 9)     │
│  └── ThreadsController [Authorize] (Fase 9, anti-IDOR)      │
│      ├── GET /api/v1/threads/{id}/messages → Historial      │
│      └── DELETE /api/v1/threads/{id}/messages → Vaciar Vita │
├─────────────────────────────────────────────────────────────┤
│  Application (MediatR)                                      │
│  ├── ChatCommand / ChatCommandHandler                       │
│  ├── StreamChatCommand / StreamChatCommandHandler           │
│  └── SendChatFeedbackCommand / Handler + Validator (Fase 9) │
├─────────────────────────────────────────────────────────────┤
│  Infrastructure                                             │
│  ├── IAiServiceClient (interfaz)                            │
│  ├── AiServiceClient (HTTP + Polly resilience)              │
│  └── HttpClientResilienceExtensions (retry + circuit breaker)│
└─────────────────────────────────────────────────────────────┘
                              │
                              ▼ HTTP
┌─────────────────────────────────────────────────────────────┐
│              AI Service (Python, Puerto 8000)                │
│  POST /api/v1/chat          → Respuesta JSON completa       │
│  POST /api/v1/chat/stream   → SSE stream (text/event-stream)│
│  POST /api/v1/chat/feedback → Feedback 1-5 (adaptive memory)│
└─────────────────────────────────────────────────────────────┘
```

## Decisiones de diseño

| Decisión                  | Razón                                                                     |
| ------------------------- | ------------------------------------------------------------------------- |
| **HTTP client (no gRPC)** | AI Service es Python/FastAPI. HTTP es más simple y compatible.            |
| **Polly resilience**      | Retry con backoff exponencial + circuit breaker para tolerancia a fallos. |
| **SSE para streaming**    | Server-Sent Events es estándar, compatible con todos los navegadores.     |
| **MediatR handlers**      | Consistencia con patrón CQRS del resto de la aplicación.                  |
| **JWT en API**            | Solo usuarios autenticados pueden usar el chat.                           |

## Flujo de chat síncrono

```
1. POST /api/v1/chat { "message": "hola" }
2. ChatController → MediatR ChatCommand
3. ChatCommandHandler → IAiServiceClient.ChatAsync()
4. HTTP POST a AI Service /api/v1/chat
5. AI Service procesa (guardrails → agent → tools → END)
6. Respuesta JSON { "reply": "...", "threadId": "..." }
7. Retornar ChatResponse al cliente
```

## Flujo de chat streaming

```
1. POST /api/v1/chat/stream { "message": "hola" }
2. ChatController → MediatR StreamChatCommand
3. StreamCommandHandler → IAiServiceClient.StreamRawAsync()
4. HTTP POST a AI Service /api/v1/chat/stream
5. AI Service retorna SSE stream
6. Controller lee línea por línea (IAsyncEnumerable)
7. Cada línea se envía al cliente como text/event-stream
8. Cliente recibe eventos: start, token, node, done, error
```

## Historial de thread (proxy de lectura, blindaje anti-IDOR Fase 9)

La app móvil reconstruye la conversación tras un re-login con
`GET /api/v1/threads/{threadId}/messages` (`ThreadsController`):

```
1. [Authorize] a nivel de controlador: solo JWT válido (401 sin sesión).
2. El dueño del thread sale estricta y exclusivamente del JWT
   (ClaimTypes.NameIdentifier); el query param `userId` se conserva por
   compatibilidad pero se IGNORA siempre — nunca define identidad ni alcance.
3. Backend → AI Service GET /api/v1/threads/{thread_id}/state?user_id=…&limit=…&before=… (canal interno X-Internal-Key).
4. Respuesta: { threadId, messageCount, lastMessage, messages: [{ role: "user"|"bot", text }], hasMore, nextCursor, historyRevision, clearedAt, resetReason }
5. Paginación hacia atrás: `before` saltea mensajes visibles desde el más nuevo y `nextCursor` es el `before` de la próxima página.
6. Si el AI Service falla o el thread no existe → 200 con estado vacío (nunca 500).
```

Contrato aditivo: `messages` viaja en orden cronológico y el AI Service
devuelve la página solicitada (paginación por `limit`/`before`, tope 100
visibles). Un ai-service anterior no envía el campo: el backend lo degrada a
lista vacía sin romper `messageCount` ni `lastMessage`.

## Limpieza del historial Vita (manual y mensual)

Contrato coordinado: `openspec/changes/vita-asistente-integral/HISTORIAL-MENSUAL.md`
del workspace. `DELETE /api/v1/threads/{threadId}/messages` requiere un JWT válido
con audiencia `app` y propietario UUID en `sub` (mapeado a
`ClaimTypes.NameIdentifier`). El servidor ignora cualquier autoridad de usuario
en query, cuerpo o cabeceras del cliente. No requiere perfil clínico activo ni
consentimientos de lectura/chat/voz: retirar el consentimiento no impide borrar.
El bypass de consentimiento es una opción interna del cliente HTTP, exclusiva
de esta operación DELETE; no es una cabecera aceptada del usuario.

BACK envía `DELETE /api/v1/threads/{thread_id}/state?user_id=…` a AI con
`X-Internal-Key`, sin cuerpo. AI comprueba el namespace del propietario y es
autoridad del borrado de checkpoints, mensajes y vínculos de voz. BACK no borra
tablas clínicas ni crea un historial paralelo.

Confirmación pública (HTTP 200, camelCase):

```json
{
  "threadId": "thread-synthetic",
  "historyRevision": 3,
  "clearedAt": "2026-11-01T05:00:00Z",
  "resetReason": "manual"
}
```

AI devuelve exactamente estos campos en snake_case. BACK exige HTTP 200, hilo
coincidente, revisión entera no negativa, fecha válida y motivo `manual|monthly`.
Un 202/204, JSON inválido o confirmación incompleta nunca se convierten en éxito.
La idempotencia y la revisión persistida las controla AI: BACK no incrementa ni
inventa metadatos y permite repetir la petición tras un fallo de transporte.

| Resultado de AI/transporte | Respuesta pública DELETE |
| --- | --- |
| Confirmación válida | 200 con los cuatro campos |
| Propiedad rechazada / hilo ausente | 403 / 404, sin detalle privado |
| Otro error HTTP, confirmación inválida o incompleta | 502 ProblemDetails |
| Error de red | 503 ProblemDetails |
| Timeout sin cancelación del cliente | 504 ProblemDetails |
| Cancelación del cliente | Se propaga; middleware existente usa 499 si puede responder |

Los errores no exponen cuerpos de AI ni credenciales; nunca devuelven una
confirmación local. Un timeout no permite saber si AI alcanzó a borrar: el
cliente debe reintentar y usar la confirmación persistida, no asumir éxito.
`CancellationToken` se propaga desde HTTP hasta envío y lectura de respuesta AI.

GET añade `historyRevision` (entero inicial 0), `clearedAt` y `resetReason`
(null antes de una limpieza). Conserva todos los campos anteriores, incluso
`voiceSyncStatus` cuando se solicita sincronización autorizada de voz. AI legado
sin metadatos conserva defaults `0/null/null`. Un hilo limpiado puede devolver
`messageCount=0`, `lastMessage=null`, `messages=[]` con revisión mayor: la app debe
invalidar su caché por revisión, aunque no exista último mensaje. La degradación
histórica de GET ante fallo sigue en 200 vacío con revisión 0; ese fallback no
acredita una limpieza y no debe reemplazar una revisión mayor ya conocida.

La retención mensual corresponde a AI: primer día del mes a las 00:00 de
`America/Bogota`, exclusivamente conversaciones Vita opt-in de app, con mes
persistido, comprobación al abrir/turno y barrido periódico. BACK solo proyecta
los metadatos. Planes, resultados, documentos y registros clínicos se conservan.
La app termina la llamada, confirma localmente la limpieza manual e invalida
peticiones/streams, mensajes y cursores manteniendo `threadId`.

Apertura app y registro de retención (integración AI `f95a3be`): BACK añade
`supports_vita=true` al GET interno cuando el JWT contiene `aud=app`. Esa señal
no se enlaza a query, body ni cabeceras del cliente y nunca se añade desde ERP.
Se envía con el GET normal y con `sync_voice=true` si la voz está autorizada.
Si se retiró el consentimiento de voz o falla el sync, el fallback normal sigue
enviando `supports_vita=true`: una conversación que solo se abre también queda
registrada para la retención mensual de AI. La señal no concede permiso de
procesamiento de voz ni añade un consentimiento de lectura. DELETE mantiene su
bypass específico de retirada.

El cliente incorpora sobrecargas explícitas de seis argumentos (incluido token
y `supportsVita`), conservando las firmas anteriores y sus parámetros opcionales.
Las llamadas anteriores omiten la nueva señal; el JSON público y la paginación
no cambian. Pruebas de esta ampliación: 91 correctas, 0 fallos, 0 omitidas con
el filtro `FullyQualifiedName~ThreadHistory|FullyQualifiedName~ThreadsVoiceSync|FullyQualifiedName~ThreadsControllerAntiIdor|FullyQualifiedName~AiServiceClientTests`.
Incluyen JWT firmado app/ERP, suplantación por query/body, voz revocada, señal
independiente de sync y regresión del borrado sin consentimiento. Transporte AI
simulado; root verifica el registro durable y barrido real al integrar AI.

Pruebas locales:

```powershell
dotnet test tests/CoppAddresd.UnitTests --filter "FullyQualifiedName~ThreadHistory"
```

Cubren JWT firmado (ausente, vencido,
firma incorrecta, audiencia y propietario), suplantación por body/query/header,
propiedad enviada a AI, confirmación/errores, consentimiento retirado,
compatibilidad GET y cancelación HTTP hasta transporte. Usan únicamente fixtures
sintéticos y transporte AI simulado, sin BD ni proveedor de voz. No acreditan el
borrado real de checkpoints, retención o aislamiento entre réplicas de AI;
root debe verificar AI→BACK→app con los commits integrados antes de publicar.

Validación de esta ampliación en `vita-history-back` (2026-11-14):

| Comando | Resultado local |
| --- | --- |
| `dotnet build --no-restore --nologo` | 0 errores, 0 advertencias (incremental; build inicial: 13 advertencias en archivos ajenos) |
| `dotnet test tests/CoppAddresd.UnitTests --no-restore --filter "FullyQualifiedName~ThreadHistory" --logger "console;verbosity=minimal"` | 50 correctas, 0 fallos, 0 omitidas |
| `dotnet test tests/CoppAddresd.UnitTests --no-restore --logger "console;verbosity=minimal"` | 2595 correctas, 0 fallos, 105 omitidas |
| `git diff --check` | Sin errores |

La suite completa se ejecutó antes de corregir únicamente el nombre del setting
de consentimiento del nuevo host de pruebas (`LegalConsents`); las 50 específicas
se repitieron después con enforcement activo. Las omitidas pertenecen a pruebas
existentes que requieren PostgreSQL. No se ejecutó integración contra AI/voz ni
se aplicaron migraciones. Postman CLI 1.76.0 responde `unknown command 'status'`;
la evidencia se conserva en pruebas .NET locales, sin publicar recursos Postman.

## Feedback del chat (Fase 9, adaptive memory)

El paciente califica una respuesta del asistente con
`POST /api/v1/chat/feedback` (`ChatController`, `[Authorize]`):

```
1. POST /api/v1/chat/feedback { executionId, threadId, rating (1-5), comment? }
2. ChatController inyecta el userId desde el JWT en el comando
   (el body nunca define quién califica) → MediatR SendChatFeedbackCommand
   (validado: execution/thread requeridos, rating 1-5, comment ≤2000).
3. SendChatFeedbackCommandHandler → IAiServiceClient.SendFeedbackAsync()
   (propaga CancellationToken).
4. HTTP POST al AI Service /api/v1/chat/feedback con
   { execution_id, thread_id, rating, comment, user_id } + X-Internal-Key.
5. Respuesta: { threadId, rating, experienceSaved, outcome }.
```

Errores (sin filtrar trazas internas, mensaje clínico amigable):

| Caso                                             | HTTP                    | `detail`                                                                              |
| ------------------------------------------------ | ----------------------- | ------------------------------------------------------------------------------------- |
| AI Service responde error (`AiServiceException`) | 502 Bad Gateway         | "El asistente inteligente no está disponible temporalmente. Intente en unos minutos." |
| AI Service offline/red (`HttpRequestException`)  | 503 Service Unavailable | El mismo mensaje clínico.                                                             |
| Sin JWT                                          | 401 Unauthorized        | "Usuario no identificado."                                                            |

Todo en `ProblemDetails` (RFC 7807).

## Resilience (Polly)

```csharp
// Configuración en HttpClientResilienceExtensions.cs
.AddResiliencePolicy()
  → Retry: 3 intentos, backoff exponencial (2^n segundos)
  → Circuit Breaker: 5 fallos consecutivos → abrir por 30 segundos
```

**Escenarios**:

- AI Service temporalmente lento → retry ayuda
- AI Service caído → circuit breaker abre, falla rápido sin saturar
- AI Service vuelve → circuit breaker cierra automáticamente

## Errores y auto-re-sincronización (resiliencia de negocio)

El AI Service responde 404 cuando el agente aún no está en su registry
(`ai.agent_runtime_configs`): la activación de versión sincroniza la config
vía `/internal/agents/sync-config`, pero el sync es best-effort y puede
fallar si el AI Service está caído en ese momento. Para no exponer ese 404
al cliente como error genérico:

- `AiServiceClient` lanza `AiServiceException` (StatusCode + Detail) en lugar
  de `EnsureSuccessStatusCode` (`ThrowForResponseAsync`).
- `ChatCommandHandler` y `StreamChatCommandHandler` detectan el 404 con
  `AgentTypeId` y reintentan UNA vez tras re-sincronizar la config activa del
  agente con `AgentRuntimeReconciler.TryResyncAsync` (transaccional: el agente
  con su `ActiveVersion` se sincroniza al runtime). En streaming el retry se
  hace sobre el primer `MoveNextAsync` (el enumerador lazy no permite
  try/catch con yield).
- `ChatController` traduce `AiServiceException` → 502 con el detalle real del
  AI Service; cualquier otra excepción → 500 "AI service unavailable".

## Configuración

```json
{
  "AiService": {
    "BaseUrl": "http://localhost:8000",
    "ApiPrefix": "/api/v1",
    "TimeoutSeconds": 120
  }
}
```

**Timeout**: 120 segundos para permitir respuestas largas del LLM.

## DTOs

### Request

```csharp
record ChatRequestDto(string Message, string? Agent = null, string? ThreadId = null, string? AgentTypeId = null, string? Language = null);
```

**`language`** (2026-10-19): idioma de la app del paciente (`"es"` | `"en"`) enviado
por `antares-paciente` en cada turno (body JSON). El gateway lo propaga al AI
Service vía `ChatRequest.language` (`AiServiceClient` lo serializa como
`language` snake_case, se omite cuando es null — compatible con AI Services
anteriores). El AI Service inyecta una instrucción explícita de idioma por
turno que gana sobre el espejo y el historial: cambiar el idioma en la app
toma efecto en la respuesta siguiente **sin crear un thread nuevo**. Null ⇒
comportamiento byte-idéntico al histórico (espejo del idioma del usuario).
record ChatRequestDto(string Message, string? Agent = null, string? ThreadId = null, string? AgentTypeId = null, string? ControlContext = null, string? Language = null);
```

`Language` ('es' | 'en', opcional): idioma de la app del paciente. Viaja al AI
Service en cada turno (`language`, omitido si null) y allí GANA sobre la regla
de espejo del prompt y sobre el historial: cambiar el idioma en la app toma
efecto en el siguiente turno del mismo thread, sin crear hilo nuevo. Con null
el payload queda byte-idéntico al histórico (backwards compatible).

### Feedback (Fase 9)

```csharp
record ChatFeedbackRequestDto(string ExecutionId, string ThreadId, int Rating, string? Comment);
record ChatFeedbackResponseDto(string ThreadId, int Rating, bool ExperienceSaved, string? Outcome);
```

### Response (síncrono)

```csharp
record ChatResponse(string Reply, string ThreadId);
```

### SSE Events (streaming)

```
event: start
data: {"threadId": "abc123"}

event: token
data: {"content": "Hola"}

event: token
data: {"content": " mundo"}

event: done
data: {}
```

## Sesión de voz (ElevenLabs)

`POST /api/v1/chat/voice/session` → `VoiceSessionCommand(UserId, PatientId,
ThreadId, Language)` → ai-service `/internal/voice/session`.

`VoiceSessionRequestDto(string? ThreadId, string? Language = null)`: el
`Language` ('es' | 'en') es el idioma de la app — el AI Service hace PATCH de
`conversation_config.agent.{language, first_message}` del agente ElevenLabs
ANTES de emitir el signed URL (con sesiones por signed URL los overrides del
cliente no aplican). Fail-open: si el PATCH falla, la sesión continúa con la
config vigente del agente. Detalle y caveats:
`ai-service/docs/elevenlabs/agent-config.md`.

## Testing

```bash
# Chat síncrono (requiere AI Service corriendo)
curl -X POST http://localhost:5122/api/v1/chat \
  -H "Authorization: Bearer <token>" \
  -H "Content-Type: application/json" \
  -d '{"message": "hola"}'

# Chat streaming
curl -X POST http://localhost:5122/api/v1/chat/stream \
  -H "Authorization: Bearer <token>" \
  -H "Content-Type: application/json" \
  -d '{"message": "hola"}' \
  --no-buffer

# Feedback (Fase 9: executionId sale de la respuesta del chat)
curl -X POST http://localhost:5122/api/v1/chat/feedback \
  -H "Authorization: Bearer <token>" \
  -H "Content-Type: application/json" \
  -d '{"executionId": "<execution_id>", "threadId": "<thread_id>", "rating": 5, "comment": "Muy útil"}'

# Historial del thread (anti-IDOR: el userId sale del JWT, el query param se ignora)
curl "http://localhost:5122/api/v1/threads/<thread_id>/messages?limit=10" \
  -H "Authorization: Bearer <token>"
```

## TODO / Mejoras futuras

- [ ] Integrar `HttpAuditActorContext` con Identity para capturar userId en auditoría
- [ ] Agregar métricas (latencia, tokens usados, errores)
- [ ] Rate limiting específico para endpoints de chat
- [ ] Caché de respuestas frecuentes (opcional)
- [ ] Soporte para uploads de archivos (imágenes, PDFs)
- [ ] Historial de conversaciones persistente (lado .NET)
- [ ] WebSockets como alternativa a SSE (bidireccional)
- [ ] Health check específico para AI Service (más allá de Polly)

## Documentos de agentes: recuperación de indexación (2026-10-08)

`POST /api/v1/agents/documents/{id}/retry` reutiliza el documento, KB y archivo
existentes. Mantiene la autenticación del controlador usada por el registro.
`RegisterAgentDocumentCommand` persiste inicialmente `Pendiente` y comparte el
mismo `AgentDocumentIndexer` que los reintentos.

El repositorio reserva la indexación con un update condicional atómico:
`Procesando` y una marca UTC de microsegundos. Una reserva vigente devuelve 409;
tras 15 minutos puede recuperarse. La finalización exige la misma marca, por lo
que un intento anterior no pisa un reintento ni recrea un documento eliminado.
El AI Service reemplaza chunks de forma idempotente por el mismo document ID.

Solo una respuesta `indexado` con chunks positivos y sin error marca `Listo`.
Respuesta inválida, timeout o fallo de archivo dejan un error recuperable y no
exponen detalles internos. La operación tiene límite de 10 minutos. Al cancelar
la petición se persiste el error con un token independiente y acotado a 10
segundos antes de propagar la cancelación; una caída del proceso requiere el
reintento manual una vez vencida la reserva.

Pruebas: `AgentDocumentIndexingTests`, `AgentDocumentIndexLeaseTests` (SQL real
SQLite), `AgentRuntimeSyncServiceTests` y `ChatFeedbackClientTests`. No requiere
migración de esquema. Las calificaciones siguen enviando `executionId` al
runtime; la asociación por respuesta se corrige allí.

## Sesión de voz (ElevenLabs)

`POST /api/v1/chat/voice/session` → `VoiceSessionCommand(UserId, PatientId,
ThreadId, Language)` → ai-service `/internal/voice/session`.

`VoiceSessionRequestDto(string? ThreadId, string? Language = null)`: el
`Language` ('es' | 'en') es el idioma de la app — el AI Service hace PATCH de
`conversation_config.agent.{language, first_message}` del agente ElevenLabs
ANTES de emitir el signed URL (con sesiones por signed URL los overrides del
cliente no aplican). Fail-open: si el PATCH falla, la sesión continúa con la
config vigente del agente. Detalle y caveats:
`ai-service/docs/elevenlabs/agent-config.md`.
