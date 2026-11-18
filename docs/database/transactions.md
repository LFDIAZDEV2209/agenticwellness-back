# Transacciones — Registro

Reglas del proyecto (skill `transactions`). Toda operación con transacción explícita se registra aquí.

## Registro

| Flujo | Qué valida | Qué escribe | Aislamiento | Duración esperada | Error | Cancelación | Doble ejecución |
|---|---|---|---|---|---|---|---|
| Auth `UserService.CreateAsync` | Email no duplicado (antes de la tx); existencia de cada rol (RoleManager) y permiso (query EF) pedidos | `auth.users` (Identity), `auth.user_roles`, `auth.user_permissions` | Read Committed | < 100 ms | Rollback total + error único (primer rol/permiso inválido o fallo de asignación); el `await using` deshace si no se commitea | Rollback en catch (o dispose) — no deja usuario ni asignaciones | Email duplicado → 23505 / mensaje amigable pre-tx; reintentar es seguro |
| Auth `UserService.UpdateAsync` | Usuario existe; si `RoleIds`/`PermissionIds` vienen (no null): existencia de cada rol/permiso | Perfil (`auth.users`), sync total de `auth.user_roles` y `auth.user_permissions`, security stamp si hubo cambios (o desactivación) | Read Committed | < 100 ms | Rollback total + error único | Rollback en catch (o dispose) — no deja sync a medias | Idempotente: sync total contra el estado actual; reintentar converge |

| Nutrición `ApproveAsync` (individual/masiva) | Alcance por profesional, revisión exacta, baterías requeridas completas bajo lock de pacientes, menú completo y ciclo vigente | Plan aprobado, asignación de 30 días, cierre de asignaciones previas, evento con actor y snapshot | Read Committed + `FOR UPDATE` de pacientes en orden UUID | Una operación SQL por grupo de datos; lote máximo 100 pacientes, sin red externa | Cualquier conflicto/validación revierte todo el lote; la estrategia recarga entidades nuevas en cada reintento | Token propagado a locks, queries, SaveChanges y commit; dispose revierte | Revisión optimista y exclusión de ciclo vigente: la segunda aprobación no duplica el ciclo |
| Nutrición `UpdateNutritionPlan` | Plan existente y revisión cargada por el editor | Reemplazo opcional de comidas, cambio a pending_review, revocación de aprobación y evento de modificación | Read Committed + revisión optimista | Solo base de datos; sin proveedor IA | Rollback de comidas y metadatos juntos ante error/409 | Token propagado en toda la unidad | El request con revisión anterior obtiene409 |

## Aprovisionamiento Auth (2026-11-17)

`UserService.CreateCoreAsync` guarda cuenta Identity, estado inicial, roles
globales/directos, acceso ERP explícito y eventual rol scoped en una transacción
Read Committed. El CSV reutiliza esa unidad por fila; una fila inactiva nunca
se publica temporalmente activa. Falta de aplicación o asignaciones inválidas
revierte todo y limpia el tracking antes de la siguiente fila.

`UserService.LinkAsync` guarda unión de roles/permisos y acceso ERP juntos;
conserva credenciales y accesos móviles. Una suspensión devuelve conflicto.
Las invitaciones internas guardan usuario nuevo o adopción, acceso ERP e
invitación en una transacción; el correo se envía después del commit.
Cancelación/error revierte por dispose; no hay red externa dentro de la tx.
Repetir una creación produce conflicto de email; repetir una vinculación
converge sin duplicar acceso. No se modifican suspensiones ni SessionVersion.

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

## Retiro y ciclo nutricional (3.2)

WithdrawAsync ejecuta inscripción + ciclo vigente en Read Committed dentro de la
estrategia retriable. Lock de paciente antes de mutar inscripción, consistente
con publicación nutricional. La transición condicional de inscripción evita dos
retiros; fallo de la actualización del ciclo revierte también la inscripción.
No hay red ni reescritura de snapshots/historial. El reloj inyectado permite
probar límites locales y continuidad de forma reproducible.

## Adherencia por ciclo (3.3)

NutritionCycleReadStore usa Repeatable Read READ ONLY: cabecera/sucesor,
versiones publicadas históricas, conteos de comidas y registros por rango forman
un snapshot consistente. El guard se ejecuta antes de entrar; cada query vuelve
a acotar por el paciente resuelto. Transacción corta sin servicios externos ni
cargas de fotos; cancelación aborta la lectura y nunca deja escrituras.

## LogNutrition con clave (3.6)

Read Committed, lock paciente → inscripción, igual orden que publicar/retirar.
Validación de dueño precede a cualquier mutación y cubre POST, replay y PUT.
Hábito/intake/XP/clave se confirman juntos; la PK por paciente/clave es el backstop
contra duplicados. El replay devuelve XP0 sin SaveChanges; una clave con otro
contenido se rechaza. Un fallo tras guardar XP pero antes de guardar la clave
revierte todo y limpia/restaura solo el tracking introducido por la operación.
No hay red ni firma de URLs en la transacción.
## Revisión diaria nutricional (3.8a)

Marcar/desmarcar toma primero `app.patient_profiles FOR UPDATE`, como POST/PUT de
ingesta y publicar/retirar. Bajo ese lock valida calendario, modifica la marca y
lee el estado derivado; commit atómico, cancelación revierte. El actor y alcance
se comprueban antes del lock; no se consulta Auth ni almacenamiento dentro de él.
`clock_timestamp()` en marcas y trigger de ingesta evita desorden por relojes de
aplicación o por transacciones abiertas antes de obtener el lock. Las consultas
de rango usan Repeatable Read READ ONLY; el contador es una sola sentencia SQL
con el filtro de alcance aplicado antes de agrupar.

## Ejemplos nutricionales anclados (3.8b)

Pin/Unpin comprueba permiso y alcance de ciclo y registro antes de abrir la
transacción. El lock `patient_profiles FOR UPDATE` serializa pin concurrente y
cambios de calendario/ingesta; dentro se valida atribución y se hace un solo
upsert/delete, sin I/O externo. Actor del JWT, hora PostgreSQL, token propagado
a lock, consulta, escritura y commit. Fallo/cancelación revierte la operación.
La lista paginada usa Repeatable Read READ ONLY; lee la comida actual por join,
sin consultas por ítem ni copia que pueda quedar obsoleta tras una corrección.

## Cierre profesional de nutrición (3.9)

El cierre posee transacción y lock de paciente. `NutritionCaseStore.ExecuteAsync`
se incorpora a la transacción existente y confirma el mismo lock sin limpiar el
tracking; no confirma ni abre otra transacción. Publicación de n+1, revisión de n,
resumen congelado tras atribución final, estado del ciclo, aviso in_app/dedupe y
bitácora se confirman o revierten juntos. El fallo forzado al escribir el evento
de publicación demuestra rollback de todos ellos y reintento en el mismo contexto.
La estrategia de la transacción externa limpia tracking entre intentos.

Archivos: I/O fuera del lock, autorización revalidada después de subir, alta y
revisión optimista juntas en DB. El fallo limpia el original con token propio de
cinco segundos, incluso si se canceló la petición. Desadjuntar retira la referencia
en DB y luego limpia el objeto; un fallo de limpieza nunca expone el huérfano.
Firmas y lectura de almacenamiento no se ejecutan dentro de la transacción SQL.

## Borrador IA del ciclo a pedido (3.13)

Snapshot de entradas con Repeatable Read READ ONLY. Tras cerrarlo se llama por
HTTP sin transacción abierta; el hash y las entradas conservan ese instante aun
si después se corrige una ingesta. Antes de persistir se revalidan permisos y
alcance; bajo el lock de paciente se comprueba de nuevo revisión optimista y
estado editable. El borrador, su revisión y bitácora se confirman juntos.
Edición/publicación concurrente produce 409; revocación devuelve 404. Fallo de IA
o cancelación no escriben ni borran el borrador previo. `insufficient` guarda la
trazabilidad, sin texto a confirmar; la publicación manual sigue siendo atómica
con el plan siguiente. No hay red dentro de los locks ni cambios de mensaje
aprobado, notas o calendario causados por la IA.


## Control de medicamentos (circuito manual)

Cada mutación usa la estrategia de ejecución de EF/Npgsql y una transacción corta
con `SELECT … FOR UPDATE` sobre el paciente. Alta + pauta + horarios + estado
inicial, confirmación + dosis + reserva de operación + evento, y cambio de estado
+ evento efectivo se confirman o revierten juntos. El tracker se limpia antes de
cada reintento; no hay HTTP, archivos ni notificaciones dentro del lock.

La habilitación por paciente usa el mismo lock y se revalida antes de las
escrituras propias: una retirada concurrente no admite una escritura obsoleta.
La revisión optimista evita correcciones perdidas. Las reservas aceptadas incluyen
no-ops: doble confirmación no añade un evento clínico, pero tampoco deja su clave
reutilizable para otro cuerpo o dosis.

Las lecturas de workspace proyectan ocurrencias sin guardar datos, incluso para
personal de solo lectura. La pauta y los estados efectivos determinan UUID y
estado de cada horario; una confirmación autorizada materializa únicamente esa
dosis y comprueba de nuevo el horario dentro de la transacción. Abrir o no un día
no altera el historial ni la cantidad de filas persistidas.

`patient_medications` permanece separado del seguimiento nuevo. El PUT propio
antiguo guarda escalares sin reemplazar colecciones; el PUT general del ERP
reconcilia los medicamentos por catálogo y conserva IDs/fechas de los existentes.
Las migraciones del circuito son aditivas y registran auditoría en ocho tablas.


## Medicamentos: existencias, alertas y seguimiento

`InPatientTransactionAsync` usa READ COMMITTED, execution strategy y lock FOR
UPDATE del paciente. Orden: paciente → tratamiento/dosis/suministro → movimientos
+operación idempotente → auditoría → refresco de alertas → commit. No hay HTTP ni
envíos dentro de la transacción. Stock, eventos clínicos, correcciones y auditoría
se confirman juntos. Consultas de reglas por rango se resuelven por lotes, no una
query por dosis/día. Procesos distintos comparten el lock; solicitudes repetidas
usan UUID/hash y revisiones esperadas. La cancelación deshace toda la escritura.
Lecturas semanales/inventario no materializan dosis; solo añaden el acceso a la
bitácora. Un conteo físico usa supply_revision para cerrar conciliaciones previas.
