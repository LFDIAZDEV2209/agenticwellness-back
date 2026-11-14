# Avisos de ciclo por cerrar — 3.11

Decisión de Luis: destinatarios **profesionales asignados**, en la bandeja de
Nutrición del ERP; no se notifica al paciente por este evento.

El tick de reconciliación existente de `NutritionDraftGenerationWorker` registra
los avisos una vez por minuto. No añade hosted services, colas ni tablas. Reutiliza
`app.notification_dedupe_keys`, con clave opaca
`nutrition-cycle:{cycleId}:closing:{userId}` y fecha de creación como entrega al
inbox. No guarda nombres, fotos ni texto clínico en esa clave; tampoco marca
un push como enviado ni usa canales externos.

Se registra durante la ventana existente `por_renovar`: faltan de cinco a cero
días para el fin, según la zona del paciente. La constante de cinco días se
comparte con `NutritionStateMachine.DisplayState`, sin cambiar ese contrato.
Solo ciclos publicados/comenzados, no revisados ni interrumpidos, sin sucesor
publicado. Destinatarios: asignación activa, profesional con empleado activo,
no eliminado y usuario vinculado. Una zona no reconocida por PostgreSQL no emite
avisos; las reglas habituales de publicación validan las zonas del paciente.

Una sentencia `INSERT SELECT … ON CONFLICT DO NOTHING` aplica el criterio y la
unicidad por ciclo/destinatario de forma atómica. Dos réplicas o reintentos no
duplican filas. El fallo de emisión se registra sin datos clínicos y no bloquea
la reconciliación/generación nutricional del worker.

`GET /api/v1/nutrition/cycle-closing-notices?page=1&pageSize=20` exige JWT ERP,
`Nutrition.View` e `IPatientScopeGuard`. La identidad y filtro vienen del actor,
sin parámetros de usuario/profesional. Respuesta `{ items, total, page, pageSize }`;
tamaño 1–100 y orden por entrega descendente con UUID como desempate.

Cada ítem contiene `id`, `cycleId`, `patientId`, `patientName`, `cycleNumber`,
`endDate`, `notifiedAt`, `titleEs`, `titleEn`, `messageEs`, `messageEn`.
El cliente abre la ficha del paciente/ciclo usando esos IDs. Este es el contrato
de avisos de la bandeja de Nutrición; no cambia el inbox de asignación de programas.

El texto se proyecta a partir de las filas actuales **después** del filtro de
alcance; la consulta filtra antes de contar y paginar. Una asignación revocada
oculta el aviso, aunque la clave permanezca. Admin exceptúa la asignación pero
solo ve sus propios avisos como destinatario. El aviso deja de listarse al revisar,
interrumpir o publicar sucesor; la clave se conserva para no volver a emitirlo.
Permiso consultado antes de la transacción Repeatable Read READ ONLY de la lista,
sin I/O de Auth dentro de ella. Respuesta sin caché.

Sin migración. Se mantienen las tablas y contratos de 3.1–3.4.

```powershell
dotnet build --no-restore -p:OutputPath=bin/Validation311/ -v quiet
dotnet test tests/CoppAddresd.UnitTests --no-build -p:OutputPath=bin/Validation311/ -v normal
dotnet test tests/CoppAddresd.IntegrationTests --no-build -p:OutputPath=bin/Validation311/ --filter 'FullyQualifiedName~NutritionPlanWorkflowTests' -v normal
```

Pruebas nuevas: límite de medianoche local, concurrencia/idempotencia, varios
profesionales asignados, empleado inactivo, revocación y contador sin ajenos,
Admin, resolución del ciclo, paginación, permiso/cancelación y fallo de emisión
que no bloquea el worker. Integración visual pendiente de front mediante
Playwright, con el contrato anterior y sin afirmar envíos push.

Evidencia 3.11: build verde, unitarios **2602 aprobados/105 omitidos**;
PostgreSQL **116/116** en 8 min21s, suite `NutritionPlanWorkflowTests` completa,
con ambas ConservaPreedicion y Fase 2. La entrega es persistencia/consulta ERP;
el caso nuevo no crea notificaciones del paciente ni realiza envíos externos.
