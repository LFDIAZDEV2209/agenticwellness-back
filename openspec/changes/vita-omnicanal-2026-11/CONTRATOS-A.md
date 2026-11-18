# Contratos internos de Vita · Ola A

Rutas GET de `/api/v1/internal/vita` bajo `RequireAiInternalKey`. El runtime envía
`X-Internal-Key` y agrega `userId` (UUID del usuario autenticado de su sesión)
fuera de los argumentos de la tool. El backend resuelve el perfil activo que
pertenece a ese usuario y comprueba el consentimiento vigente de `chat`.
No se aceptan selectores de paciente. Cada consulta usa exclusivamente el perfil
resuelto y es de solo lectura. Las respuestas con datos llevan
`Cache-Control: no-store`.

## Fechas, valores y errores

- Fechas de calendario `YYYY-MM-DD` en la zona `timeZone` del paciente. Los
  instantes (`asOf`, `observedAt`, `scheduledAt`, `occurredAt`) se expresan en
  UTC en ISO 8601. `asOf` representa el instante de lectura.
- Decimales y enteros son números JSON. Un dato ausente se representa con
  `null` o `[]`, según el campo. Las mediciones se ordenan por fecha de
  observación descendente. En empate se usa fecha de registro descendente e ID
  descendente. Se excluyen las observaciones posteriores a `asOf`.
  Cada punto tiene `metricCode`, `value`, `unitCode`,
  `unitSymbol`, `observedAt`, `source` y `measurementId`. La unidad se toma de la
  lectura. Las mediciones heredadas sin registro canónico llevan `measurementId: null`.
- **200** con el DTO del recurso, incluso si sus secciones están vacías.
- **400** para `userId` ausente, inválido o vacío, fechas inválidas o intervalo
  fuera del rango admitido. La validación HTTP responde con Problem Details.
- **401** con `{"message":"Clave interna inválida."}` si falta una clave
  interna válida. Una identidad pública no sustituye la clave.
- **403** con Problem Details y `detail: "VITA_PATIENT_UNAVAILABLE"` si no
  hay un perfil activo para ese usuario, y con
  `detail: "VITA_MEDICATIONS_FORBIDDEN"` si su acceso a medicación fue
  deshabilitado. La comprobación de consentimiento usa el contrato existente
  `{"code":"CONSENT_REQUIRED","scope":"chat"}` cuando falta el consentimiento.
- **404** con Problem Details si `Vita:InternalEnabled` está apagado o el
  módulo `Medications:Enabled` está apagado para la ruta de medicación.
- **503** con `{"code":"CONSENT_DOCUMENT_UNAVAILABLE","scope":"chat"}` si no se puede
  verificar el consentimiento.

Ejemplo de **403** para una sesión sin perfil autorizado.

```json
{
  "type":"https://httpstatuses.io/403",
  "title":"Forbidden",
  "status":403,
  "detail":"VITA_PATIENT_UNAVAILABLE",
  "instance":"/api/v1/internal/vita/profile",
  "correlationId":"qa-synthetic"
}
```

Ejemplo de **404** para el módulo de medicación deshabilitado.

```json
{
  "type":"https://httpstatuses.io/404",
  "title":"Not Found",
  "status":404,
  "detail":"Módulo no disponible.",
  "instance":"/api/v1/internal/vita/medications",
  "correlationId":"qa-synthetic"
}
```

## Perfil · 3.1

`GET /profile?userId={uuid}`

Perfil mínimo y edad en años cumplidos. `ageYears` y `gender` pueden ser nulos.
`goals` contiene las metas nutricionales registradas en la prescripción
publicada vigente, o en el plan personal aprobado y asignado vigente si falta
la prescripción. `goals` es nulo si no hay plan con metas. Sus campos numéricos
son opcionales mediante `null`. `targetCondition` es nulo si no se registró.
`allergies` contiene nombres registrados del catálogo. `restrictions` contiene
el texto registrado para el plan vigente cuando existe, en un elemento.
Los campos de antropometría `weight`, `height` y `bmi` son el último punto de
cada métrica, o `null`. No se calcula un IMC ausente. `history` contiene hasta
100 puntos de peso, talla e IMC, con `hasMore` si se truncó. Solo en esta
sección, las mediciones heredadas de peso o talla pueden tener `source: null`
porque el registro anterior carece de procedencia individual.

```json
{
  "asOf": "2026-11-18T15:00:00Z",
  "profile": {"firstName":"Paciente","ageYears":38,"gender":"F","timeZone":"America/Bogota"},
  "goals": {
    "source":"nutrition_prescription",
    "targetCondition":null,
    "dailyCalorieTarget":1800,
    "dailyProteinTargetG":100,
    "dailyCarbsTargetG":200,
    "dailyFatTargetG":60,
    "dailyFiberTargetG":25
  },
  "allergies":["Alérgeno de prueba"],
  "restrictions":[],
  "anthropometry": {
    "weight":{"metricCode":"weight","value":74.5,"unitCode":"kg","unitSymbol":"kg","observedAt":"2026-11-18T12:00:00Z","source":"patient","measurementId":"07686a2d-c782-4e7b-b410-e4a74d99e720"},
    "height":{"metricCode":"height","value":168,"unitCode":"cm","unitSymbol":"cm","observedAt":"2026-10-30T14:00:00Z","source":null,"measurementId":null},
    "bmi":null,
    "history":[
      {"metricCode":"weight","value":74.5,"unitCode":"kg","unitSymbol":"kg","observedAt":"2026-11-18T12:00:00Z","source":"patient","measurementId":"07686a2d-c782-4e7b-b410-e4a74d99e720"},
      {"metricCode":"height","value":168,"unitCode":"cm","unitSymbol":"cm","observedAt":"2026-10-30T14:00:00Z","source":null,"measurementId":null}
    ],
    "hasMore":false
  }
}
```

En `goals.source` los valores son `nutrition_prescription` y
`nutrition_plan`. Códigos de antropometría usuales `weight/kg`,
`height/cm` y `bmi/kg_m2`.

## Signos vitales y biometría · 3.2

`GET /vitals?userId={uuid}&from=2026-11-12&to=2026-11-18`

`from` y `to` son fechas locales inclusivas, con un máximo de 31 días. Por
defecto `to` es hoy y `from` es seis días antes de `to`. `latest` tiene
como máximo un punto por métrica dentro de ese intervalo, aunque el historial
se trunque. `history` contiene los 200 puntos más recientes del intervalo.
`hasMore` indica truncamiento. Los arreglos están vacíos si no hay lecturas.

| Métricas | Unidad usual |
|---|---|
| `heart_rate`, `o2_saturation` | `bpm`, `pct` |
| `systolic_bp`, `diastolic_bp`, `temperature_c` | `mmhg`, `mmhg`, `celsius` |
| `sleep_minutes`, `step_count`, `activity_kcal`, `distance_m` | `minutes`, `count`, `kcal`, `meters` |
| `glucose_fasting`, `body_fat` | `mg_dl`, `pct` |

`source` proviene de la medición clínica y es `device`, `patient`,
`professional` o `lab`. Solo se muestran mediciones confirmadas de la tabla
clínica. La incorporación de signos vitales heredados sin fuente pertenece a la
tarea 4.6.

```json
{
  "asOf":"2026-11-18T15:00:00Z",
  "timeZone":"America/Bogota",
  "from":"2026-11-12",
  "to":"2026-11-18",
  "latest":[
    {"metricCode":"heart_rate","value":68,"unitCode":"bpm","unitSymbol":"bpm","observedAt":"2026-11-18T12:00:00Z","source":"device","measurementId":"67c9101e-f14d-4004-9a67-090f27cb22c7"}
  ],
  "history":[
    {"metricCode":"heart_rate","value":68,"unitCode":"bpm","unitSymbol":"bpm","observedAt":"2026-11-18T12:00:00Z","source":"device","measurementId":"67c9101e-f14d-4004-9a67-090f27cb22c7"}
  ],
  "hasMore":false
}
```

## Medicación y nutracéuticos · 3.3

`GET /medications?userId={uuid}`

Tratamientos activos y vigentes en la fecha local de su régimen. `localDate`
identifica el día del paciente en `timeZone`. `times` son horarios
`HH:mm` según la zona del régimen. `doses` contiene las ocurrencias de ese
tratamiento dentro del día del paciente. Se reutilizan los estados del módulo
`pending`, `taken`, `omitted` y `cancelled`. `occurredAt` puede ser
nulo. `productType` es `medication`, `vitamin` o `supplement`. `endDate` puede ser nulo.
`doseId` identifica la ocurrencia exacta para una captura de Vita.
`route` puede ser una cadena vacía. Se omiten las anotaciones libres y el
historial de otros días.

```json
{
  "asOf":"2026-11-18T15:00:00Z",
  "timeZone":"America/Bogota",
  "localDate":"2026-11-18",
  "treatments":[{
    "id":"872523df-2f71-4dbd-8cc5-792cd0a7943e",
    "name":"Producto de prueba",
    "productType":"supplement",
    "dosage":1,
    "unit":"cápsula",
    "route":"oral",
    "timeZone":"America/Bogota",
    "times":["08:00","20:00"],
    "startDate":"2026-11-12",
    "endDate":null,
    "doses":[
      {"doseId":"c0718fec-43eb-4fe6-a9cb-973a33a69547","scheduledAt":"2026-11-18T13:00:00Z","status":"taken","occurredAt":"2026-11-18T13:05:00Z"},
      {"doseId":"742b25d3-3d44-40e3-84bb-08a5f5d52227","scheduledAt":"2026-11-19T01:00:00Z","status":"pending","occurredAt":null}
    ]
  }]
}
```

## Ciclo nutricional e historial · 3.4

`GET /nutrition?userId={uuid}&date=2026-11-18`. `date` es una fecha local
opcional y por defecto es hoy. Una fecha futura o inválida devuelve 400.
`plan` es el ciclo publicado del paciente vigente en esa fecha, o `null`.
El calendario cubre la ventana publicada, hasta 30 días. Cada día lleva
`date`, `status` (`planned` o `unavailable`) y `meals` con los campos
`mealType`, `description`, `foods`, `calories`, `proteinG`, `carbsG`,
`fatG`, `fiberG`, `waterMl`, `notes`, `sortOrder`, `suggestedTime` y
`notesEn` del menú que ve el paciente. `status` se refiere a disponibilidad
del menú. El consumo registrado se devuelve por separado.
`history` cubre los 30 días locales hasta `date`, inclusive. Sus lecturas
conservan `null` para los nutrientes que no fueron reportados.
`recommendations` contiene hasta 12 mensajes publicados y visibles al paciente.

```json
{
  "date":"2026-11-18","plan":null,"recommendations":[],
  "history":[{"date":"2026-11-18","mealCode":"alm","calories":420,"proteinG":null,"carbsG":null,"fatG":null,"fiberG":null,"waterMl":null,"source":"vita_chat"}]
}
```

Si hay ciclo, `plan` tiene `id`, `revision`, `startDate`, `endDate` y
`days`. Cada recomendación tiene `cycleNumber`, `publishedAt` y
`patientMessage`. Los arreglos vacíos representan ausencia de datos con 200.

## Baterías · 3.5

`GET /assessments?userId={uuid}` devuelve `batteries`, hasta 20 asignaciones
recientes. Cada batería tiene `id`, `name`, `status`, `dueDate`,
`completedAt` y `tests`. Una batería pendiente o en curso lleva `tests: []`.
Una batería completada incluye las evaluaciones completadas cuyos resultados
están disponibles para el paciente. Cada evaluación tiene `testName`,
`testCode`, `completedAt`, `score`, `scorePercentage` y `results`.
Cada resultado tiene `resultType`, `code`, `label`, `value`, `qualifier`
y `severity`. Las subescalas conservan el puntaje y omiten la interpretación
cualitativa. Las respuestas originales y las notas internas quedan fuera.

```json
{"batteries":[{"id":"a2baa408-9421-4741-8f21-c810a01a7b73","name":"Batería inicial","status":"pending","dueDate":null,"completedAt":null,"tests":[]}]}
```

## Documentos del expediente · 3.6

`GET /documents?userId={uuid}&page=1&pageSize=20` devuelve `items`, `total`,
`page` y `pageSize`. Tamaño de página 1–50, página 1–10.000.
`GET /documents/{id}?userId={uuid}` devuelve `document`, `url` y
`expiresInSeconds` (120). La URL puede ser relativa al backend en storage local.
La respuesta para un documento ajeno, eliminado o no disponible es 404.
Las URL son capacidades temporales de lectura y no deben enviarse al usuario
como texto de conversación ni guardarse en memoria.

```json
{"items":[{"id":"97609143-62df-4946-b3b9-01e11bd6f613","name":"Documento de ejemplo","type":"Informe","date":"2026-11-18T13:00:00Z","status":"Ready"}],"total":1,"page":1,"pageSize":20}
```

```json
{"document":{"id":"97609143-62df-4946-b3b9-01e11bd6f613","name":"Documento de ejemplo","type":"Informe","date":"2026-11-18T13:00:00Z","status":"Ready"},"url":"https://storage.example/documento?signature=example","expiresInSeconds":120}
```

Las tres lecturas aplican los errores comunes de identidad, consentimiento y
clave interna, incluida la respuesta 404 si `Vita:InternalEnabled` está apagado.
Las respuestas con datos llevan `Cache-Control: no-store`.

## Captura confirmada · 4.2 a 4.5

Las dos rutas exigen `Vita:InternalEnabled`, `Vita:CaptureEnabled`, la clave
interna y el consentimiento de chat. El runtime agrega `userId` en la consulta.
El runtime mantiene el `channel` y el `turnId` de la captura original cuando
recibe la confirmación en otro turno de conversación. `channel` es
`vita_chat`, `vita_voice` o `vita_whatsapp`. `turnId` tiene de 1 a 256
caracteres sin espacios al inicio ni al final.

### Proponer

`POST /capture/propose?userId={uuid}` devuelve 200 y un token opaco de cinco
minutos. Valida los datos y la pertenencia del recurso sin modificar la BD.
Usa instantes ISO 8601 con offset y devuelve los instantes en UTC. `observedAt`
es requerido y debe corresponder a un instante ya ocurrido.

Ejemplo para `record_measurement`. `supersedesId` es opcional y, cuando se
envía, debe apuntar a una medición confirmada del propio paciente, de la misma
métrica y unidad, reportada por el paciente y todavía vigente.

```json
{"tool":"record_measurement","channel":"vita_voice","turnId":"turno-17","metricCode":"weight","value":74.5,"unitCode":"kg","observedAt":"2026-11-18T08:00:00-05:00"}
```

| `metricCode` | `unitCode` | Rango de entrada inclusivo |
| --- | --- | --- |
| `weight` | `kg` | 1–500, hasta dos decimales |
| `heart_rate` | `bpm` | 20–250, entero |
| `systolic_bp` | `mmhg` | 50–260, entero |
| `diastolic_bp` | `mmhg` | 20–180, entero |
| `o2_saturation` | `pct` | 30–100, entero |
| `temperature_c` | `celsius` | 30–45, hasta dos decimales |

Una temperatura de 45 requiere confirmación adicional y queda marcada para
revisión. Los valores fuera del rango de entrada se rechazan.

Para `record_medication_dose`, `doseId` y `scheduledAt` provienen de
`GET /medications`. `status` debe ser `taken` u `omitted`.
`observedAt` es el instante reportado de la toma u omisión. La captura
respeta el acceso al módulo y las reglas de disponibilidad de la ocurrencia.
Una toma ya registrada con un estado distinto se corrige desde el módulo.

```json
{"tool":"record_medication_dose","channel":"vita_chat","turnId":"turno-24","doseId":"c0718fec-43eb-4fe6-a9cb-973a33a69547","scheduledAt":"2026-11-18T13:00:00Z","status":"taken","observedAt":"2026-11-18T13:05:00Z"}
```

Respuesta de propuesta. Los campos opcionales de `data` ajenos al tipo de
captura son nulos.

```json
{
  "token":"v1.token.opaco.ejemplo",
  "expiresAt":"2026-11-18T13:10:00Z",
  "requiresAdditionalConfirmation":false,
  "data":{"tool":"record_measurement","metricCode":"weight","value":74.5,"unitCode":"kg","observedAt":"2026-11-18T13:00:00Z","supersedesId":null,"doseId":null,"scheduledAt":null,"status":null,"program":null}
}
```

`record_meal` y `record_checkin` usan la inscripción activa resuelta por el
backend. `observedAt` determina el día local de esa inscripción. El token
fija el recurso resuelto y la fecha. El objeto `data.program` de la propuesta
contiene `enrollmentId`, `localDate`, `meal` y `moodScore`.

Una comida usa `meal.mealCode` (`des`, `alm`, `mer` o `cen`).
`calories` (kcal), `proteinG`, `carbsG`, `fatG` y `fiberG` (g) son
opcionales. Admite hasta dos decimales y los mismos límites del registro
nutricional. Una comida ya registrada devuelve 409. El canal se guarda como
origen del registro y el recibo vincula la captura con la fila de nutrición.

```json
{"tool":"record_meal","channel":"vita_chat","turnId":"comida-12","observedAt":"2026-11-18T13:00:00-05:00","meal":{"mealCode":"alm","calories":420,"proteinG":24}}
```

El check-in usa `moodScore` entero de 1 a 5 y la tarea emocional programada
del día. Devuelve 409 si el check-in ya se registró o ya no está disponible.
Ambas capturas conservan los puntos del programa y su comportamiento de replay.

```json
{"tool":"record_checkin","channel":"vita_voice","turnId":"checkin-18","observedAt":"2026-11-18T18:00:00-05:00","moodScore":4}
```

### Confirmar

`POST /capture/confirm?userId={uuid}` se invoca tras una confirmación explícita
del paciente. Si `requiresAdditionalConfirmation` es verdadero, el runtime
solicita otra confirmación y envía también `confirmedUnusual: true`. Una
cancelación o un cambio de tema no llama a esta ruta.

```json
{"token":"v1.token.opaco.ejemplo","channel":"vita_voice","turnId":"turno-17","confirmed":true}
```

La respuesta 200 contiene identificadores estables. Un replay válido de la misma
captura devuelve el mismo resultado. `entityType` es `clinical_measurement`,
`medication_dose`, `nutrition_intake_log` o `daily_checkin`.

```json
{"receiptId":"02eb1189-cc15-4001-b1de-5784dc6179b8","entityType":"clinical_measurement","entityId":"07686a2d-c782-4e7b-b410-e4a74d99e720"}
```

| HTTP | Motivo |
| --- | --- |
| 400 | Payload, rango, unidad o fecha inválidos, confirmación ausente o token alterado/expirado. |
| 401 | Clave interna ausente o inválida. |
| 403 | Paciente distinto, identidad inactiva, consentimiento ausente, módulo de medicamentos deshabilitado para el paciente o intento de corregir una medición ajena o de otro origen. |
| 404 | Rutas de captura apagadas, módulo de medicamentos apagado, ausencia de inscripción activa u ocurrencia no disponible. |
| 409 | La clave se usó con otros datos, el canal o turno no coinciden, falta la confirmación adicional, la ocurrencia o inscripción cambió, la medición ya tiene un reemplazo o ya existe una comida o un check-in para ese día. |
| 422 | El módulo canónico rechaza la fecha o no dispone de la configuración necesaria. |
| 503 | No se puede verificar el consentimiento. |
