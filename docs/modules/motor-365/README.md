# Persistencia del Motor 365

`AppDbContext` y la migración `AddMotor365Persistence` definen el esquema
`app` para el motor v5.2. [ADR del Motor 365](../../decisions/2026-11-24-motor-365-dotnet.md)

| Tabla | Identidad y uso |
| --- | --- |
| `person` | `patient_id` es la PK y FK al perfil canónico. Contiene sexo, año de nacimiento, antropometría inicial, peso meta opcional, actividad, objetivo, ritmo, preferencias, fecha inicial y versiones. |
| `consent` | Vincula el paciente con el recibo existente de `app.legal_document_acceptances`. El recibo conserva el documento publicado, fecha, ámbito y revocación. La escritura futura valida que su usuario corresponda al perfil y que ambos recibos estén vigentes. |
| `screening` | `(person_id, submission_id)` identifica las 7 respuestas PAR-Q y 6 NQS. Cada respuesta admite 0, 1 o NULL. `answers_complete` es una columna generada. Todo NULL impide publicar la caché para ese envío mediante una FK compuesta al paciente y cuestionario completo. El worker debe comprobar la misma compuerta antes de invocar el cálculo y resolver las indicaciones clínicas aplicables. |
| `daily_log` | `(person_id, day)` y `(person_id, log_date)` son únicos. `log_date` es la fecha nominal del ordinal del plan. El día efectivo de cierre usa la zona local y el ancla de [KAN-131](../wellness-365/README.md#calendario-y-registros). El registro incluye adherencia, horas de sueño, energía, dolor, síntomas, peso/cintura opcionales y ejercicio completado. Los síntomas de referencia son `mareo`, `hambre`, `fatiga` e `insomnio`. La migración `AllowMotor365NauseaObservation` admite registrar también `nauseas` sin aplicar una regla adicional. Una lista vacía indica que no se reportaron síntomas. |
| `engine_params` | Cada versión es un objeto JSON, con versión de motor, autor, fecha y razón. Publicar un cambio implica insertar una nueva versión. |
| `plan_snapshot` | Caché reconstruible con clave `(person_id, plan_version, params_version)`, envío fuente y versión de motor. El servidor avanza la versión al aceptar nuevas entradas. Solo reutiliza la fila de la revisión y parámetros vigentes si corresponde al día y a las entradas verificadas. |
| `engine_event` | Decisiones v5.2 con persona, día, versión de plan, motor y parámetros, mensaje bilingüe y ajuste cuando aplica. |
| `audit_log` | Persona, acción, fecha y actor opcional. El actor por defecto usa `audit.user_id` que propaga el interceptor existente cuando hay contexto transaccional. El escritor del flujo registra las acciones de manera explícita. |
| `professional_review` | FK al par persona/envío de `screening` y a `erp.professionals`, con decisión, fecha y nota opcional. La autorización del profesional conserva el alcance clínico existente. |

La base impide valores fuera de rango para día, adherencia, sueño, energía,
dolor, actividad, antropometría y respuestas binarias. Los límites físicos de almacenamiento
son estatura hasta 300 cm, peso hasta 1.000 kg y cintura hasta 500 cm. La
elegibilidad clínica corresponde al motor. Las FK de historial restringen
borrados físicos con registros dependientes. El rollback elimina exclusivamente
los objetos del módulo y sus datos.

## Pago, envío y Home

`paymentId → userId → patientId → submissionId → generationId`. La activación
y membresía pertenecen a KAN-116. El cuestionario v5.2 usa el par
`patientId, submissionId` del recorrido existente. La generación de
`11a44836` conserva el envío, intentos, lease y resultado de muestra en
`app.wellness_journey_generations`. Home puede mostrar `pending` o `running`
desde el envío aceptado y recuperar el mismo `generationId` con `latest`
después de recargar. El trabajo en segundo plano publica `ready` con los planes
y la bandeja o `failed` con reintento sobre el mismo ID.

El demo garantiza una generación por `payment_id` y rechaza cambios de
respuestas con `409`. El contrato propuesto usa una generación por
`(patientId, submissionId)` y contempla envíos versionados. La política para
un segundo pago de la misma persona y para corregir un envío se resuelve al
conectar el Motor 365 real. Las claves nuevas permiten correlacionar el envío
sin agregar dependencias a la migración de generación. La integración futura
debe reutilizar el envío, su idempotencia y el mismo ámbito de paciente/pago
al cambiar a resultados reales.
[Contrato de generación](https://github.com/LFDIAZDEV2209/coppAddresdBack/blob/11a44836/docs/architecture/wellness-365-generation-contract.md)

## Auditoría y alcance

La migración revoca `UPDATE`, `DELETE` y `TRUNCATE` a `PUBLIC` y al rol
`app_user` si existe, igual que el rol usado en la migración de signos
vitales. El trigger rechaza las tres operaciones incluso si el ejecutor
tiene los privilegios por propiedad o pertenencia a otro rol. En entornos
normales, usar un propietario de DDL separado del rol de ejecución y
confirmar el rol efectivo antes del despliegue. Reservar los cambios de
estructura al migrador.

Las FK aíslan las asociaciones por paciente. Las futuras lecturas y
mutaciones resuelven el perfil propio por JWT o por el alcance clínico del
ERP y filtran por ese paciente. El owner debe decidir el límite del tenant
y su relación con pacientes que reciben atención en varias clínicas antes
de habilitar RLS. Si se aprueba, el cambio requiere mapear esa pertenencia,
propagar el contexto dentro de cada transacción para pooling, separar roles
del worker y migrador, y probar accesos propios, ajenos y entre clínicas.

## SQL y prueba con PostgreSQL efímero

`db/schema.sql` es la referencia generada por `dotnet ef migrations script`
desde la migración anterior hasta `AddMotor365Persistence`. El archivo
contiene el DDL de esa migración, requiere el esquema precedente y omite la
escritura del historial de EF. Para desplegar se usa la cadena de EF.

Para ejecutar la prueba Npgsql, provisionar un PostgreSQL 16 efímero en un
puerto alternativo, crear una BD vacía con prefijo `copp_motor365_test_`
y crear el rol `app_user` como `NOLOGIN`. La conexión usa el propietario
desechable de la BD y se carga solo en el entorno de la prueba.

```powershell
$env:COP_MOTOR365_TEST_DB_CONNECTION = 'Host=127.0.0.1;Port=55434;Database=copp_motor365_test_local;Username=<owner>;Password=<efimera>'
dotnet test tests/CoppAddresd.IntegrationTests --filter 'FullyQualifiedName~Motor365MigrationPostgresTests'
Remove-Item Env:COP_MOTOR365_TEST_DB_CONNECTION
```

La prueba ejecuta los scripts Up y Down de EF sobre prerrequisitos mínimos,
verifica checks, cruces de paciente, privilegios y trigger de auditoría, y
compara inventario e historial con el estado anterior. Descartar la BD al
terminar.

## API v1 (KAN-130)

La API de CoppAddresd publica el OpenAPI del kit adaptado a las identidades
del backend en `/swagger/motor365/swagger.json`. Los recursos de persona,
planes, cierres y eventos viven bajo `/v1/people`. Los parámetros, catálogos,
auditoría y sonda de estado se publican bajo el mismo prefijo. El día se
calcula con la zona del perfil. El plan anual no incluye menús. El día
individual devuelve los cuatro menús y la sesión queda vacía cuando está
en pausa.

El perfil propio se resuelve desde el JWT. El ERP comprueba rol funcional,
permiso y alcance de paciente. Los términos y la privacidad se verifican
contra recibos vigentes del backend. Si el formulario requiere revisión,
se consulta la última decisión vinculada al envío antes de calcular.
El servidor fija las versiones al publicar cada revisión y mantiene el
historial de cambios y decisiones.

El flujo de generación asíncrona existente conserva sus rutas y estados.
La [tabla de operaciones, roles, errores y gates](../../../.ai-coord/KAN130.md)
documenta el contrato, la prueba optativa en BD efímera y las decisiones de
integración pendientes.

### Sesión de ejercicio del día (KAN-125)

`GET /api/v1/people/me/plan` y la lectura equivalente por paciente para ERP
incluyen `exercise`, con el patrón, la fase nominal, cuatro ejercicios, minutos
por ejercicio, dosis de fuerza, descripciones y guías ES/EN. Un plan pausado
devuelve `exercise: null`. La app no reconstruye una sesión local cuando existe
un plan del Motor 365. Los desbloqueos, la fase y la descarga se calculan por
la semana real del plan. El día 365 pertenece a la semana 52.

El plan anual `/v1/people/{id}/plan` conserva el formato compacto. En el plan
real, una racha puede variar la rotación, pero nunca adelantar un desbloqueo.
La dosis de masa usa las mismas series por nivel que los otros objetivos, con
RIR 1–2. Se conserva la simulación original del kit para la paridad v5.2. Los
snapshots anteriores a la revisión de esta política se reconstruyen en la siguiente
lectura anual sin cambiar los catálogos, las versiones de parámetros ni el esquema.

Para reproducir el recorrido, instalar las dependencias de la app y del host
de pruebas. Desde `tests/CoppAddresd.Wellness365.E2E`, configurar
`COP_KAN130_APP_DIR` con la ruta absoluta al checkout de la app y
`COP_KAN131_TEST_DB_CONNECTION` con PostgreSQL efímero, como se describe abajo.
Ejecutar `npm run test:exercise`. Se comprueban 365 días para cada objetivo y
la pantalla de ejercicio de la app en español e inglés.

## Caché y observabilidad (KAN-128)

El plan anual busca la PK de `app.plan_snapshot` con la versión actual del
plan y de parámetros. Antes de atender un hit, la API vuelve a comprobar el
alcance, los recibos vigentes y la elegibilidad. El contenido debe coincidir
con el envío, motor, edad y día efectivo. Los cierres y cambios de perfil
avanzan la revisión. Publicar parámetros invalida las lecturas siguientes
sin modificar el perfil. Cambiar el día o la edad efectiva reemplaza el
artefacto de la misma clave. El formato anterior se trata como miss.

En un miss anual la API calcula y guarda la proyección. El día individual
y los cierres evalúan solo la línea temporal hasta hoy. Los cierres guardan
decisiones en su transacción y, después del commit, pueden encolar el plan
anual. Las lecturas diarias también lo encolan. La cola local tiene capacidad
de 128 personas, un consumidor y deduplicación por persona en espera o en
proceso. Si se llena o se reinicia la instancia, la lectura anual sigue
reconstruyendo el plan. El worker vuelve a resolver el contexto vigente.
No se hace un barrido masivo al cambiar parámetros. El upsert es idempotente
entre réplicas y no reemplaza un artefacto producido desde una vista más nueva.

Los snapshots no tienen TTL por tiempo. Se invalidan lógicamente según las
entradas y el día. Las revisiones históricas existentes no se eliminan por
este cambio. La persistencia de la caché no es requisito para devolver el
plan calculado si falla su escritura. Las lecturas de planes usan
`REPEATABLE READ` para ver entradas y versiones coherentes y solo guardan
cambios del ancla de calendario. Las escrituras de datos de negocio
conservan el aislamiento serializable.

El meter y la fuente de trazas se llaman `CoppAddresd.Motor365`. La latencia y
los errores se separan por operación (`annual`, `day`, `calendar`, `events` y
`background`). Se registran bloqueos de elegibilidad, ajustes al confirmar
una revisión, resultados de caché anual y fallos de escritura de caché. Las
métricas incluyen la versión de motor y no incluyen identificadores de persona.
Las trazas de cálculo usan un HMAC truncado a 128 bits de la identidad.
Configurar `Motor365__TraceKey` con el mismo secreto aleatorio en las instancias
si se requiere correlación entre ellas. Sin configuración la clave es efímera
por proceso. El colector del entorno debe suscribirse al meter y ActivitySource.
Los logs de aplicación redactan las rutas de paciente y no registran
perfiles, identificadores directos, tokens ni el contenido de los planes.

### E2E de caché y carga

La suite Playwright optativa usa el host de pruebas de KAN-131 en Release y
un PostgreSQL local vacío con prefijo `copp_motor365_test_` y puerto alternativo.
El navegador llama a los endpoints reales de `/v1` con JWT sintéticos. No se
simulan el cálculo, el almacenamiento, el control de versiones ni el JSON
devuelto. La página del fixture no representa la app ni el ERP desplegados.

Desde `tests/CoppAddresd.Wellness365.E2E`, con PostgreSQL efímero y las
variables disponibles solo para el proceso de pruebas:

```bash
npm ci
npx playwright install chromium
export COP_KAN131_TEST_DB_CONNECTION='Host=127.0.0.1;Port=55438;Database=copp_motor365_test_kan128;Username=<owner>;Password=<efimera>'
npm run test:kan128
COP_KAN128_LOAD=1 PLAYWRIGHT_JSON_OUTPUT_FILE=kan128-results.json npm run test:kan128 -- --reporter=list,json
```

Descartar la BD al terminar. Usar una BD nueva para comparar corridas de
rendimiento. La suite escribe revisiones y publica parámetros de prueba.

- **Frescura.** Cambia el perfil, escribe un cierre y publica parámetros por
  API. Un `200` con datos anteriores falla de inmediato. Solo se reintenta
  un `503` marcado como reintentable mientras termina el recálculo. El
  cambio de parámetros se verifica sin forzar otra escritura del paciente.
- **Carga.** Siembra 1.000 personas sin snapshots y usa ocho solicitudes
  simultáneas. Mide el p95 cliente desde el envío hasta leer y decodificar
  el JSON. Hace un pase anual y uno diario, y luego repite ambos. El primer
  pase diario puede reutilizar el trabajo del anual. Los cuatro pases
  validan versiones y exigen p95 menor a 300 ms para el año y 150 ms para
  el día. El JSON incluye el número de respuestas obsoletas por pase.
- **Medición de caché.** El fixture escucha un contador `Counter<long>` de
  .NET con meter `CoppAddresd.Motor365`, instrumento
  `motor365.plan.cache.requests` y etiqueta `result=hit|miss`. La relación
  es `hits / (hits + misses)` de las lecturas anuales, sin contar el worker.
  Sin muestras se informa `null`, no 0 %. No se infieren hits por
  latencia ni por la existencia de una fila en la BD.

La suite también comprueba que el worker materializa el snapshot después
de una lectura diaria, que el plan anual no conserva el día previo y que
un snapshot caliente no evita la revocación de consentimiento. La traza
de la respuesta contiene un seudónimo, no el identificador del paciente.
El reporte se adjunta como `kan128-load.json`. La corrida local no valida
capacidad ni exportación de telemetría de un entorno desplegado.
