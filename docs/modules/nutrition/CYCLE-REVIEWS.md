# Cierre profesional del ciclo — 3.9

La revisión pertenece al ciclo **n revisado**. Decisión de Luis: publicar exige
una prescripción preparada del ciclo n+1 y publica ambos en una sola transacción.
El mensaje no se mueve al ciclo nuevo. D0014: la app muestra como `current` la
última recomendación por `published_at`, incluso si el ciclo revisado terminó;
`previous` contiene las anteriores por publicación descendente.

## API del cierre

Prefijo `/api/v1/nutrition/cycles`; todas las rutas resuelven el paciente dueño
mediante `IPatientScopeGuard`. Ajeno → 404. Admin exceptúa asignación, no permiso.

- `GET {id}/review`: `Nutrition.View`; devuelve null si aún no hay revisión.
- `POST {id}/review`: `Nutrition.Approve`; guarda y permite retomar el borrador.
- `POST {id}/publish`: `Nutrition.Approve`; comprueba también el dueño de la
  prescripción siguiente y exige que sea el mismo paciente.

Guardar:

```json
{
  "revision": 0,
  "professionalNotes": "Evaluación interna",
  "patientMessage": { "es": "Mensaje aprobado", "en": "Approved message" },
  "criticalAlertsConfirmed": true,
  "aiDraftConfirmed": false
}
```

Revisión inicial 0; cada escritura devuelve la nueva `revision`. Una revisión
obsoleta obtiene 409. Se puede guardar incompleto; publicar exige notas de
evaluación, mensaje es/en y alertas confirmadas (pasos 4 y 6). Si existe
`ai_draft_json`, también exige confirmación expresa del borrador actual.
El cliente nunca envía `summary_json` ni `ai_draft_json` como mensaje aprobado.

Publicar:

```json
{ "revision": 3, "nextPrescriptionId": "uuid", "nextPrescriptionRevision": 4 }
```

La prescripción siguiente debe estar preparada para aprobar (`en_aprobacion`),
ser un ciclo distinto del mismo paciente y no estar publicada. Se reutiliza
`NutritionCases`/Approve: validación del menú, alertas, fechas locales de 3.2,
revisión optimista y eventos del editor. El ciclo revisado debe haber terminado
en la zona del paciente y no tener sucesor publicado. La revisión pasa a
publicada e inmutable; el ciclo pasa a revisado (interrumpido se conserva).
Repetir la publicación de esa revisión con la misma prescripción devuelve el
resultado persistido sin nuevas publicaciones, avisos ni eventos.

Respuesta ERP: `cycleId`, `revision`, `reviewedAt`, `publishedAt`,
`professionalNotes`, `patientMessage` bilingüe, `criticalAlertsConfirmed`,
`aiDraftConfirmed`, `summary`, `aiDraft`, `attachments`. Notas y borrador IA se
ocultan sin `Nutrition.NotesView`; adjuntos y sus URLs se ocultan sin
`Nutrition.PhotosView`, comprobado antes de firmar. No es el DTO móvil de 3.10.
El nombre del autor se toma del empleado vinculado al usuario autenticado; queda
vacío si el actor autorizado no tiene perfil de empleado, sin inventar una identidad.

El resumen incluye calendario/adherencia de 3.3 y medias diarias de kcal, macros
y agua. Un dato ausente queda null, no cero. Se recalcula bajo lock al publicar,
después de fijar el inicio del sucesor: el día de inicio de n+1 no se atribuye
también a n. Después queda congelado aunque el paciente corrija una comida.

## Adjuntos privados

- `POST {id}/review/attachments`: multipart `file`, `revision`, `caption`
  opcional, `visibleToPatient` (por defecto false). Requiere Approve y PhotosView.
  Devuelve `{ revision, attachment: { id, kind, caption, visibleToPatient, url } }`.
- `DELETE {id}/review/attachments/{attachmentId}?revision=N`: requiere Approve
  sobre ciclo y adjunto, PhotosView y pertenencia al mismo ciclo/paciente.
  Solo borradores editables. Devuelve `{ revision }`.

Máximo 10 MB por archivo, pie de hasta 2000 caracteres; extensión, MIME y firma
deben coincidir para JPEG/PNG/WebP/PDF. No se aceptan claves ni IDs de revisión
elegidos por el cliente. Se generan claves privadas canónicas bajo
`documents/{patient}/{attachment}/original.ext`, reutilizando las operaciones
exclusivas de archivo privado del proveedor. El proxy y los métodos genéricos de
storage no pueden leer, firmar ni sobrescribirlas como archivos públicos.

Subida fuera de la transacción, revalidación de permiso/alcance tras subir y
persistencia con revisión optimista bajo lock. Ante rollback se limpia el original
con un token acotado de compensación. Una limpieza fallida deja un huérfano privado
y un aviso técnico sin claves ni datos clínicos. Tras commit nunca se borra un
archivo referenciado por un fallo al construir la respuesta.

`GET /api/v1/nutrition/attachments/{id}/content?sig&exp` consume una capacidad
de cinco minutos emitida solo tras el guard ERP (o, en 3.10, identidad del paciente
y visibilidad publicada). Firma HMAC con clave derivada propia, ligada al ID;
una firma genérica, otro ID o más de cinco minutos se rechazan antes de leer BD.
Se verifica que la clave sea el original de ese paciente/adjunto. Sin caché,
nosniff; PDF como descarga. El endpoint no emite firmas ni acepta escrituras.
Solo imágenes visibles llegarán al DTO móvil; documentos e imágenes internas no.

## Persistencia y validación

Migración aditiva `20261114200555_AddNutritionCycleReviews`: tablas
`nutrition_cycle_reviews` (única por ciclo) y `nutrition_cycle_attachments`, FKs
Restrict y comentario de reversa. Sin backfill ni alteración de las tablas
anteriores. SQL Up/Down revisado; Up aplicado solo a PostgreSQL local. Down se
bloquea si hay notas/revisiones/adjuntos que deban conservarse.

Guardar, publicar y modificar adjuntos dejan entrada semántica en
`audit.activity_logs`, reutilizando `WriteAuditRowAsync`: actor, fecha y evento.
La publicación añade un aviso al centro de notificaciones de la app, con clave
`nutrition-cycle:{cycleId}:review-published` en `notification_dedupe_keys`.
No se afirma entrega FCM: el registro persistido es del canal `in_app`.

```powershell
dotnet build --no-restore -p:OutputPath=bin/Validation39/ -v quiet
dotnet test tests/CoppAddresd.UnitTests --no-build -p:OutputPath=bin/Validation39/ -v normal
dotnet test tests/CoppAddresd.IntegrationTests --no-build -p:OutputPath=bin/Validation39/ --filter 'FullyQualifiedName~NutritionPlanWorkflowTests' -v normal
```

Aplicación local: `Get-Content -Raw -Encoding UTF8 $env:TEMP/copp-39-up.sql |
docker exec -i coppAddresd psql -v ON_ERROR_STOP=1 -U app_user -d coppaddresd`.

Evidencia 3.9: build verde, unitarios **2598 aprobados/105 omitidos**;
PostgreSQL **114/114**, suite completa en 9 min52s con ambas ConservaPreedicion
y Fase 2. Casos nuevos: pasos incompletos, IA sin confirmar, alcance de ciclo/
prescripción/adjunto, revisión obsoleta, rollback del plan y aviso por fallo de
bitácora, reintento, resumen congelado, ocultación de notas/fotos, validación de
archivo, compensación y alta explícita del adjunto. Firma de propósito distinto,
ID, caducidad y descarga protegida verificadas con controlador real en unitarios.
No hay evidencia de UI ni envío FCM en este carril; contrato comunicado a front
para Playwright del flujo, estados y tamaños solicitados por Luis.
