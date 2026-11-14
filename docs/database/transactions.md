# Transacciones — Registro

Reglas del proyecto (skill `transactions`). Toda operación con transacción explícita se registra aquí.

## Registro

| Flujo | Qué valida | Qué escribe | Aislamiento | Duración esperada | Error | Cancelación | Doble ejecución |
|---|---|---|---|---|---|---|---|
| Auth `UserService.CreateAsync` | Email no duplicado (antes de la tx); existencia de cada rol (RoleManager) y permiso (query EF) pedidos | `auth.users` (Identity), `auth.user_roles`, `auth.user_permissions` | Read Committed | < 100 ms | Rollback total + error único (primer rol/permiso inválido o fallo de asignación); el `await using` deshace si no se commitea | Rollback en catch (o dispose) — no deja usuario ni asignaciones | Email duplicado → 23505 / mensaje amigable pre-tx; reintentar es seguro |
| Auth `UserService.UpdateAsync` | Usuario existe; si `RoleIds`/`PermissionIds` vienen (no null): existencia de cada rol/permiso | Perfil (`auth.users`), sync total de `auth.user_roles` y `auth.user_permissions`, security stamp si hubo cambios (o desactivación) | Read Committed | < 100 ms | Rollback total + error único | Rollback en catch (o dispose) — no deja sync a medias | Idempotente: sync total contra el estado actual; reintentar converge |

| Nutrición `ApproveAsync` (individual/masiva) | Alcance por profesional, revisión exacta, baterías requeridas completas bajo lock de pacientes, menú completo y ciclo vigente | Plan aprobado, asignación de 30 días, cierre de asignaciones previas, evento con actor y snapshot | Read Committed + `FOR UPDATE` de pacientes en orden UUID | Una operación SQL por grupo de datos; lote máximo 100 pacientes, sin red externa | Cualquier conflicto/validación revierte todo el lote; la estrategia recarga entidades nuevas en cada reintento | Token propagado a locks, queries, SaveChanges y commit; dispose revierte | Revisión optimista y exclusión de ciclo vigente: la segunda aprobación no duplica el ciclo |
| Nutrición `UpdateNutritionPlan` | Plan existente y revisión cargada por el editor | Reemplazo opcional de comidas, cambio a pending_review, revocación de aprobación y evento de modificación | Read Committed + revisión optimista | Solo base de datos; sin proveedor IA | Rollback de comidas y metadatos juntos ante error/409 | Token propagado en toda la unidad | El request con revisión anterior obtiene409 |

## Reglas permanentes

`LegalConsentService` uses a short explicit transaction within Npgsql's execution
strategy for acceptance/withdrawal. A per-user advisory transaction lock serializes
idempotency and scope withdrawal; acceptance also takes a shared legal-document
row lock against publication. It validates the current published UUID and approved
disclosure, writes the receipt and trigger audit atomically, and commits. No HTTP
or storage I/O occurs inside the transaction. Cancellation/failure rolls back;
retries reload tracking and the idempotency key returns the original receipt.
The explicit transaction also installs the JWT audit actor for one-row writes.

- Una sola escritura atómica → **no** necesita transacción explícita.
- 2+ escrituras todo-o-nada → `BeginTransactionAsync` + `CommitAsync`, `await using`, rollback en catch.
- **Nunca** dentro de la transacción: HTTP externo, email, S3, SQS, I/O de usuario (locks retenidos).
- Aislamiento default `Read Committed`; `Repeatable Read`/`Serializable` solo con justificación + retry por `40001`.
- Duración objetivo < ~100ms; transacciones largas = locks = contención/deadlocks.
- Retry con backoff en `40001` (serialization) / `40P01` (deadlock), nueva transacción + datos re-leídos.
- Publicación de eventos post-commit: patrón outbox si se necesita entrega garantizada (a decidir cuando exista el primer caso).

## Asignación post-tests (paquete 2)

El submit usa su transacción clínica actual y bloquea la asignación para
serializar clientRequestId. Tras commit/dispose publica BatteryCompletedEvent.
El consumidor usa una transacción independiente Read Committed, dentro de la
estrategia Npgsql, y SELECT del perfil FOR UPDATE: comprueba batería requerida
completa y ausencia de Active/PendingAssignment, lee perfil/catálogo y guarda
enrollment+StreakState+semanas Locked+recommendation. No hace HTTP dentro del lock.
La inscripción manual toma el mismo lock y comprueba ambos estados. Error o
cancelación: rollback íntegro del programa, sin afectar el submit confirmado;
el consumidor registra Warning. Después del commit invalida scores-history y
encola solo el ID de recomendación. Narrativa se procesa en otro scope fuera de
esta transacción. Las pruebas contra PostgreSQL incluyen fallo después de crear
la inscripción, rollback y dos consumidores concurrentes sin duplicación.

## Aplicación/fallback de asignación y avisos (paquete 3)

ActivateAssignmentAsync usa Read Committed con estrategia Npgsql y orden de locks
perfil → inscripción → recomendación. Comprueba estado/plazo dentro del lock,
resuelve contenido disponible, hornea snapshot, actualiza semanas y estado,
persiste el aviso al paciente y auditoría semántica antes del commit. No hace
HTTP en esa transacción. Replays terminales no escriben ni notifican.
La creación de la recomendación guarda también el inbox profesional. El job
reutiliza la división relacional de completitud y el mismo comando de aplicación.
Recoveries usa INSERT ON CONFLICT condicional para claim/backoff durable. La
entrega usa UPDATE condicional con lease y progreso sticky por canal; FCM/chat
ocurren después del commit. Una caída entre envío y confirmación admite entrega
externa repetida (al menos una vez); la decisión y el inbox sí son idempotentes.

## Nutrición: fundamentos legacy y prescripciones (grupos 1–2)

`UpdateNutritionPlanCommandHandler` recarga y valida el plan dentro de
`ExecuteInTransactionAsync`; elimina días, añade reemplazos y actualiza
plan/revisión/evento en una transacción Read Committed con estrategia Npgsql.
Se propaga el token recibido por la operación a todas las llamadas. Un error
limpia el tracking; `await using` revierte también si el token está cancelado.
Un reintento recarga entidades y revisa la versión original. No hay llamadas
externas dentro de la transacción. Objetivo: duración típica inferior a 100 ms.

`NutritionPrescriptionRepository` persiste cada agregado nuevo o actualizado en
un solo `SaveChangesAsync`, usando la transacción automática de EF: cabecera,
comidas e ítems se confirman o revierten juntos. La revisión observada participa
en el WHERE del UPDATE y una escritura concurrente produce
`DbUpdateConcurrencyException`. El caso de uso futuro traducirá ese conflicto.
El contexto de una escritura fallida se descarta al finalizar el alcance.
La integración contra PostgreSQL verifica fallo de FK tras el borrado legacy,
fallo de FK en un agregado nuevo y rollback de una revisión obsoleta.

### Ola 2: decisiones profesionales y bitácora

`NutritionCaseStore.ExecuteAsync` ejecuta en Read Committed con estrategia Npgsql
y lock de `app.patient_profiles` por paciente. Cada reintento limpia tracking,
recarga alcance/revisión y confirma versión, comidas, ítems, reemplazo de la
publicación y evento con snapshot juntos. Objetivo: transacción corta, sin IO
externo; el cálculo y la llamada a IA terminan antes de abrirla. Fallo/cancelación
revierte por dispose. Los fallos de revisión se traducen a conflicto de negocio.
La integración inyecta fallo en bitácora tras el guardado del agregado y verifica
que no persisten cabecera, hijos ni eventos.

### Nutrición Ola 6: reserva, lease y generación

Cada escritura de reserva/claim/resultado/fallo usa una transacción corta Read
Committed con estrategia Npgsql y token; BEGIN instala GUC del actor y COMMIT
los elimina. INSERT ON CONFLICT serializa replay por paciente/batería. Claim usa
un UPDATE con subquery FOR UPDATE SKIP LOCKED, sin espera entre réplicas. El
claim token impide que un worker antiguo confirme una lease tomada por otro.
IA/cálculo ocurren fuera de esas transacciones. Generate conserva la transacción
del agregado con lock del paciente; reutiliza el ID de reserva al reejecutar.
El commit de agregado precede la confirmación ready: tras un corte, el retry
relee el plan existente, no agrega otra versión ni otro evento. Cancelación
deja la lease recuperable; cinco intentos agotados quedan failed y visibles.

## Community: member blocking

`CommunityBlockPolicy` serializes directed block/unblock, follow and direct-message
writes with a short Read Committed transaction and one PostgreSQL advisory lock
for the sorted profile pair. After acquiring the lock it rechecks current block
rows; blocking removes both follow directions atomically but preserves moderation
and message history. Duplicate block/unblock converges. Cancellation/failure rolls
back through asynchronous disposal. Events publish only after commit; no external
HTTP or event publication runs while the database lock is held. The Community
connection does not enable EF retry execution strategies. No load testing is
claimed; functional tests use two concurrent synthetic requests.
# WhatsApp — claims de eventos y recordatorios (2026-11-12)

Fase 10: upsert de BindingSession y claim condicional de lease por wa_id en
sentencias cortas; reserva durable de cuota/intento antes de llamar a Auth.
La confirmación abre una transacción Read Committed, bloquea únicamente la sesión
con lease propio vigente, inserta patient_links con ON CONFLICT y guarda el estado,
respuesta y limpieza del evento. No hay HTTP dentro de esa transacción. Un lease
perdido no puede crear un vínculo; un estado de llamada incierta se cierra sin
repetir SMS/check automáticamente. La unicidad previa por paciente/número se conserva.

`UPDATE ... WHERE id = (SELECT ... FOR UPDATE SKIP LOCKED) RETURNING *` reclama
un evento en una sentencia atómica, Read Committed, lease de cinco minutos.
El lock termina antes de llamar al agente o a Meta. No hay HTTP dentro de
transacciones de BD. El cursor de fragmentos se guarda después de cada aceptación.
Cancelación deja el lease recuperable; reenvíos concurrentes usan `ON CONFLICT`.

Recordatorios: claim atómico SKIP LOCKED con lease de un minuto. Una transacción
Read Committed corta reserva contador de intento + ReminderDelivery Dispatching;
commit antes de llamar a Meta. Updates finales condicionados a Pending y lease
evitan deshacer una cancelación concurrente. El webhook actualiza actividad y
cancelaciones antes del ACK. Los hooks clínicos corren después del commit del
módulo propietario; si fallan, polling revalida y cancela antes de otro envío.

### Retiro de medios con sustitución (1.12)

`MediaRetirementStore` ejecuta lectura, sustitución de plantilla/semanas futuras y
retiro en una transacción serializable bajo la estrategia de ejecución Npgsql.
Evita carreras entre validar dependencias y retirar el medio; errores o cancelación
revierten todo. DELETE con referencias conservadas devuelve 409 y revierte también
la sustitución. Los blobs se eliminan solo después del commit y cuando no queda
ninguna referencia. Repetir archivo/despublicación conserva el resultado; ninguna
llamada externa mantiene abierta la transacción. Detalle en
`docs/modules/program-progress/MEDIA-RETIREMENT.md`.

### Asignación de podcasts a varios pacientes (1.15)

`BulkPatientPodcastStore` autoriza antes de abrir la transacción; dentro de ella
filtra pacientes, lee programa/medio/semanas y persiste el plan en una escritura
EF por lote. Serializable protege frente a activación o personalización concurrente.
La estrategia Npgsql reevalúa desde datos nuevos; limpia el tracking por intento.
Conflictos persistentes dan 409. Validación y cancelación revierten todo el lote;
repetir una selección idéntica no modifica los snapshots. No hay HTTP ni storage
dentro de la transacción. El detalle del contrato y conteos vive en
`docs/modules/program-progress/BULK-PATIENT-PODCASTS.md`.

### Archivo administrativo de default-83w (1.16)

`scripts/archive_default_83w.py` bloquea una sola fila de catálogo con FOR UPDATE
en Read Committed y cambia únicamente su estado. Persiste la preimagen fuera de
la BD antes del UPDATE; un fallo revierte la transacción y deja el respaldo como
reversa inocua. No cambia inscripciones/semanas ni hace llamadas externas.
La simulación es de solo lectura. La reversa comprueba la postimagen completa
antes de restaurar; repetir archivo o reversa no ejecuta UPDATE. Instrucciones
y evidencia local en `scripts/README-archive-default-83w.md`.

### Relleno legado (2.10)

El script de origen usa lotes de hasta 500 semanas (máximo configurable 5000),
Read Committed, lock timeout 5 s y statement timeout 60 s. El UPDATE por conjunto
compara el snapshot original y revalida fecha derivada, activación, finalización
e historial. Un conflicto concurrente se cuenta y conserva; un error revierte
el lote actual, los anteriores pueden estar confirmados. Repetir no reescribe
celdas v2. La reversa compara la postimagen antes de restaurar. No modifica
asignaciones antiguas ni consulta servicios externos. Procedimiento y ejecución
local en `scripts/README-week-snapshot-origin.md`.

### Edición del plan semanal (2.8)

Read Committed dentro de la estrategia Npgsql; bloquea inscripción y semana con
FOR UPDATE en ese orden, igual que la completación, para serializar contra el
inicio de la semana. Valida fecha local/estado/historial y referencias antes de
una única escritura del snapshot. Objetivo <100 ms; sin HTTP ni almacenamiento.
Fallo o cancelación revierte la edición completa. No escribe completaciones,
rollups XP ni asignaciones antiguas. El handler exige permiso y alcance antes
de abrir la transacción; se vuelven a leer estado y fechas bajo bloqueo.

La edición semanal 2.12 añade el evento semántico de bitácora en esta misma
transacción, después de Save y antes de Commit. Un fallo del INSERT de auditoría
revierte el snapshot; se desacopla la semana fallida del tracking. La prueba de
fallo inyectado verifica rollback y que una edición posterior no resucite cambios
fallidos. No hay tabla nueva ni llamadas externas dentro de la transacción.

La masiva de rutinas (2.16) comparte la transacción Serializable de podcasts 1.15:
autorización antes de abrirla, consultas por conjuntos, validación completa antes
de Save y un commit del lote. Conflictos 40001/23505 se traducen a 409; el intento
siguiente recarga datos y tracking. Solo escribe snapshots; los rechazos y la
cancelación no dejan aplicaciones parciales ni asignaciones antiguas.

El modo simulación de podcasts (1.15b) usa Repeatable Read con `SET TRANSACTION
READ ONLY` antes de materializar consultas. Comparte alcance, validación y
planificador; retorna antes de adjuntar entidades o Save. La protección de BD
impide escrituras accidentales. El modo normal mantiene Serializable. La vista
previa no reserva datos entre solicitudes; confirmar reevalúa el estado actual.


## Primera publicación del ciclo (3.1)

El trigger de compatibilidad ensure_nutrition_cycle usa FOR UPDATE sobre el
paciente, mismo orden que NutritionCaseStore. Crear cabecera y asignar MAX+1
son parte de la transacción de la prescripción; fallo/cancelación revierte
ambos y no consume un número. El índice paciente/número respalda el MAX y la
unicidad. No hay I/O externo. No se elimina ni renumera una cabecera al
sustituir una versión publicada. La migración limita espera de locks a 5 s.
