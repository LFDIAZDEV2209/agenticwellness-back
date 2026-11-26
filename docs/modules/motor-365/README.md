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
| `plan_snapshot` | Caché reconstruible con clave `(person_id, plan_version, params_version)`, envío fuente y versión de motor. La integración debe avanzar la versión de plan al aceptar una nueva revisión de entradas e invalidar o regenerar la caché correspondiente. |
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
