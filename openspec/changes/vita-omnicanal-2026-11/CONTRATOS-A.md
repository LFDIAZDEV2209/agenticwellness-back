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
  `unitSymbol`, `observedAt` y `source`. La unidad se toma de la lectura.
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
    "weight":{"metricCode":"weight","value":74.5,"unitCode":"kg","unitSymbol":"kg","observedAt":"2026-11-18T12:00:00Z","source":"patient"},
    "height":{"metricCode":"height","value":168,"unitCode":"cm","unitSymbol":"cm","observedAt":"2026-10-30T14:00:00Z","source":null},
    "bmi":null,
    "history":[
      {"metricCode":"weight","value":74.5,"unitCode":"kg","unitSymbol":"kg","observedAt":"2026-11-18T12:00:00Z","source":"patient"},
      {"metricCode":"height","value":168,"unitCode":"cm","unitSymbol":"cm","observedAt":"2026-10-30T14:00:00Z","source":null}
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
    {"metricCode":"heart_rate","value":68,"unitCode":"bpm","unitSymbol":"bpm","observedAt":"2026-11-18T12:00:00Z","source":"device"}
  ],
  "history":[
    {"metricCode":"heart_rate","value":68,"unitCode":"bpm","unitSymbol":"bpm","observedAt":"2026-11-18T12:00:00Z","source":"device"}
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
      {"scheduledAt":"2026-11-18T13:00:00Z","status":"taken","occurredAt":"2026-11-18T13:05:00Z"},
      {"scheduledAt":"2026-11-19T01:00:00Z","status":"pending","occurredAt":null}
    ]
  }]
}
```
