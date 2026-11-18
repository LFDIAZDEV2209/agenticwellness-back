# Módulo: Mediciones Clínicas (Clinical Measurements)

Skill: `documentation`. Módulo para persistir mediciones clínicas genéricas de pacientes
(monitoreo continuo + registro del profesional en consultas periódicas).

## Objetivo

Registrar mediciones clínicas de forma **genérica** (métrica + unidad + valor) con:

- **Monitoreo continuo**: lecturas de dispositivos/wearables y auto-reporte del paciente, sin encounter.
- **Consultas periódicas**: mediciones registradas por el profesional durante un encounter.

Modelo en tres capas: **catálogos** (qué se mide y en qué unidad) + **encounter canónico**
(contenedor de la visita) + **mediciones** (el valor con su contexto temporal y de origen).

La extracción OCR nueva de exámenes no establece mediciones clínicas: conserva el
original y candidatos con procedencia `ocr_unverified`, sin inferir fecha/unidad ni
completar controles. La comparación es aritmética y no clasifica mejoría/deterioro.
Ver [contrato de exámenes de laboratorio](../lab-exams/README.md). Los registros
manuales/profesionales y la historia existente mantienen su flujo y procedencia.

## Alcance (esta iteración)

- ✅ Entidades + configuraciones EF + migración del core (schema `app`)
- ✅ Columna `encounter_id` + FK en `tele.clinical_encounters` (migración del módulo tele)
- ✅ Seed de catálogo: unidades + métricas + rangos de referencia iniciales
- ❌ DTOs, endpoints, frontend (fase posterior)
- ❌ Métricas `test_score` (cuando se conecten los tests)

## Decisiones de arquitectura (ADR)

### ADR-001 — Encounter canónico nuevo, tele linkeado (NO mover ClinicalEncounter)

`ClinicalEncounter` (módulo tele) ya es un encounter en espíritu (patient, professional, date,
status, notes, `clinical_data` JSONB), pero nació **atado** a `TelemedicineAppointment` (1:1,
`AppointmentId` obligatorio) y vive en un proyecto separado con **DbContext e historial de
migraciones propios** (`tele.__ef_migrations_history`).

Opciones evaluadas:

| Opción                                        | Costo                                        | Riesgo |
| --------------------------------------------- | -------------------------------------------- | ------ |
| Mover ClinicalEncounter al core               | Refactor ~20 archivos + migraciones cruzadas | Medio  |
| **Encounter canónico nuevo + link (elegida)** | Bajo, tele intacto                           | Bajo   |

**Decisión**: crear `app.encounters` como encounter canónico en el core. El de tele **queda
intacto** y se **linkea** con FK nullable `encounter_id` → `app.encounters.id`. No es duplicación:
es especialización — el de tele conserva su detalle de sesión (appointment/session), el canónico
es el registro clínico general que usan las mediciones.

**Regla crítica de frontera**: la FK `tele.clinical_encounters.encounter_id` se crea en una
migración del **módulo tele** (su propio historial), NUNCA del core. El core define la entidad
canónica; el tele solo agrega la columna que apunta a ella.

### ADR-002 — Observación con dos timestamps

`observed_at` (cuándo se tomó la medición) y `recorded_at` (cuándo se registró en el sistema).
Difieren cuando el profesional carga mediciones históricas en una consulta.

### ADR-003 — Rangos de referencia por edad/género en tabla separada

Los rangos normales varían por edad y género, por eso no viven en la métrica sino en
`measurement_reference_ranges` con `age_min`/`age_max`/`gender` nullable + `priority`.

### ADR-004 — Encounter canónico liviano

El canónico NO replica el `clinical_data` JSONB del tele. Queda estructurado y liviano
(`reason` + `notes`). El detalle clínico rico permanece solo en tele.

## Entidades (schema `app`)

### `unit_of_measures`

| Campo       | Tipo         | Notas                               |
| ----------- | ------------ | ----------------------------------- |
| `id`        | uuid PK      | default `gen_random_uuid()`         |
| `code`      | varchar(50)  | único — `mg/dL`, `kg`, `cm`, `mmHg` |
| `name`      | varchar(150) |                                     |
| `symbol`    | varchar(20)  |                                     |
| `is_active` | bool         | default `true`                      |

### `measurement_metrics`

| Campo             | Tipo                         | Notas                                                          |
| ----------------- | ---------------------------- | -------------------------------------------------------------- |
| `id`              | uuid PK                      | default `gen_random_uuid()`                                    |
| `code`            | varchar(50)                  | único — `glucose_fasting`, `weight`, `systolic_bp`             |
| `name`            | varchar(150)                 |                                                                |
| `description`     | text?                        |                                                                |
| `default_unit_id` | uuid FK → `unit_of_measures` | Restrict                                                       |
| `category`        | varchar(50)                  | `vital` \| `metabolic` \| `body_comp` \| `test_score` (futuro) |
| `is_active`       | bool                         | default `true`                                                 |

Índices: `ix_measurement_metrics_code` (único), `ix_measurement_metrics_default_unit_id`.

### `measurement_reference_ranges`

| Campo                     | Tipo                            | Notas                                             |
| ------------------------- | ------------------------------- | ------------------------------------------------- |
| `id`                      | uuid PK                         | default `gen_random_uuid()`                       |
| `metric_id`               | uuid FK → `measurement_metrics` | Cascade                                           |
| `age_min` / `age_max`     | int?                            | null = todos                                      |
| `gender`                  | varchar(1)?                     | `M` / `F` / `X`, null = todos                     |
| `min_value` / `max_value` | decimal?                        |                                                   |
| `unit_id`                 | uuid FK → `unit_of_measures`    | Restrict                                          |
| `priority`                | int                             | default `0` — resuelve solapamientos (mayor gana) |
| `notes`                   | text?                           |                                                   |
| `is_active`               | bool                            | default `true`                                    |

Índices: `ix_measurement_reference_ranges_metric_id`.

### `encounters` (canónico)

| Campo                     | Tipo                         | Notas                                                                       |
| ------------------------- | ---------------------------- | --------------------------------------------------------------------------- |
| `id`                      | uuid PK                      | default `gen_random_uuid()`                                                 |
| `patient_id`              | uuid FK → `patient_profiles` | Cascade                                                                     |
| `professional_id`         | uuid FK → `professionals`    | Restrict                                                                    |
| `type`                    | varchar(30)                  | `consulta_periodica` \| `telemedicina` \| `seguimiento`                     |
| `status`                  | varchar(20)                  | `planned` \| `in_progress` \| `completed` \| `cancelled`, default `planned` |
| `started_at` / `ended_at` | timestamptz?                 |                                                                             |
| `reason`                  | text?                        | motivo de consulta                                                          |
| `notes`                   | text?                        |                                                                             |
| `created_by`              | uuid?                        |                                                                             |
| `created_at`              | timestamptz                  | default `now()`                                                             |
| `updated_at`              | timestamptz?                 |                                                                             |

Índices: `ix_encounters_patient_id`, `ix_encounters_professional_id`, `ix_encounters_status`,
`ix_encounters_type`.

### `clinical_measurements`

| Campo          | Tipo                            | Notas                                            |
| -------------- | ------------------------------- | ------------------------------------------------ |
| `id`           | uuid PK                         | default `gen_random_uuid()`                      |
| `patient_id`   | uuid FK → `patient_profiles`    | Cascade                                          |
| `metric_id`    | uuid FK → `measurement_metrics` | Restrict                                         |
| `encounter_id` | uuid FK → `encounters`          | **NULL = monitoreo autónomo**, SetNull           |
| `value`        | decimal                         |                                                  |
| `unit_id`      | uuid FK → `unit_of_measures`    | Restrict                                         |
| `observed_at`  | timestamptz                     | cuándo se tomó                                   |
| `recorded_at`  | timestamptz                     | default `now()` — cuándo se registró             |
| `source`       | varchar(20)                     | `device` \| `patient` \| `professional` \| `lab` |
| `notes`        | text?                           |                                                  |
| `created_by`   | uuid?                           |                                                  |
| `created_at`   | timestamptz                     | default `now()`                                  |

Índices: `ix_clinical_measurements_patient_id`, `ix_clinical_measurements_metric_id`,
`ix_clinical_measurements_encounter_id`, `ix_clinical_measurements_patient_observed`
(`patient_id`, `observed_at` DESC) — para el "último valor por métrica" y series temporales.

## Procedencia y recibos de Vita (Ola A)

`20261118201312_AddVitaClinicalWriteModel` agrega `source_channel`,
`reported_by`, `status`, `supersedes_id` e `idempotency_key` a
`app.clinical_measurements`. Las filas previas se leen como `confirmed`.
Si no se registró el canal o actor, los campos respectivos siguen nulos.
La FK de `supersedes_id` referencia la medición anterior. La lectura de Vita
respeta el ámbito del paciente y excluye las filas pendientes y reemplazadas.

| Índice | Uso |
| --- | --- |
| `ux_clinical_measurements_patient_idempotency` | Unicidad del par paciente/clave cuando existe clave. Permite la consulta de reintentos sin afectar filas históricas. |
| `ix_clinical_measurements_supersedes_id` | FK y búsqueda de reemplazos. Indexa las filas que apuntan a una medición anterior. |
| `ux_vita_write_receipts_patient_channel_turn_tool` | Unicidad del recibo por paciente, canal, turno y tool. El prefijo paciente sirve para la FK al perfil. |

La migración crea los índices de la tabla existente con `CONCURRENTLY` y agrega
las restricciones con `NOT VALID` antes de validarlas en un paso separado.
La ejecución por el migrador de EF respeta esos límites de transacción. Los
dos índices se reconstruyen al reintentar una aplicación interrumpida.
El `Down` conserva las filas y valores de mediciones y retira las columnas
nuevas y la tabla de recibos. Antes de usar `Down` tras habilitar capturas,
respaldar los metadatos y recibos que se necesiten conservar.

Vita escribe en esta tabla tras la confirmación explícita. Las correcciones
crean una fila con `supersedes_id` y conservan la medición previa. Vita, el
historial de métricas de la app y la vista general del ERP leen las filas
vigentes. Las lecturas por lote de laboratorio conservan sus registros.
Se invalidan las dos claves de historial de métricas después del commit.

La captura habilita peso, frecuencia cardíaca, presión, saturación y temperatura.
[Contrato de captura](../../../openspec/changes/vita-omnicanal-2026-11/CONTRATOS-A.md)

### 4.6 Consolidación de signos vitales

| Consumidor inventariado en backend | Acceso antes de la consolidación | Compatibilidad |
| --- | --- | --- |
| Expediente ERP, `PatientsController` y `PatientRepository` | GET incluye `PatientProfile.VitalSigns`. POST crea tomas y PUT las reemplaza mediante `ExecuteDeleteAsync` e inserción EF. | Mismo nombre y columnas para lectura, inserción y reemplazo. |
| App, `/api/v1/me` y `MetricsHistoryRepository` | El historial de métricas de la app y de Programa toma la talla de `VitalSigns`. La serie y las mediciones móviles usan la tabla canónica. La edición del perfil móvil solo toca escalares. | Se preserva la talla del perfil y se incorpora el histórico a la serie canónica. |
| Programa, `ProgramRepository.PersistVitalsAsync` | La tarea `vitals` ya escribe en `clinical_measurements`. Desde `20260901193540` la FK de `task_completions.vital_signs_batch_id` apunta a esa tabla. | Los lotes y anclas mantienen su origen y su FK. |
| SOS, `SosVitalsSnapshot` | Toma las últimas mediciones canónicas de origen permitido y puede complementar con el reporte explícito de la app. | El filtro de procedencia se ejecuta antes de elegir la última lectura para que una toma heredada no desplace a una lectura aceptada por SOS. |
| Reportes y reglas clínicas | `ClinicalContextService` lee la serie canónica y conserva un fallback al perfil. El detalle ERP, la biometría comunitaria y el backfill usan mediciones y preagregaciones canónicas. | El acceso por perfil continúa por la vista. Los reportes canónicos reciben el histórico consolidado. |
| Vita, `VitaClinicalReader` | El perfil combinaba peso y talla de ambas relaciones. El endpoint de signos vitales leía el modelo canónico. | Ambos leen una sola fuente. Vita muestra origen e ID de captura nulos para observaciones heredadas. |
| Importación y datos demo | `scripts/import_patients_from_excel.py` escribe por `COPY`. `seed_engagement_demo_data.py`, `seed_engagement_remaining.py` y `generated/rich_dataset_seed.sql` cuentan e insertan. `generate_rich_dataset_seed.py` genera ese SQL. | La vista acepta `COPY FROM` y `INSERT` con su trigger `INSTEAD OF`. |

La migración `20261118214000_ConsolidateLegacyVitalSigns` copia un valor por
campo no nulo y preserva la fecha observada (`measured_at`) y la fecha de
registro (`created_at`). El esquema anterior solo tenía paciente, valores y
fechas. Además, el importador y los seeds escribían en la misma relación.
Por eso se usa `source='legacy'`, `reported_by=NULL`, `source_channel=NULL`
y `status='confirmed'`. La clave de archivo `source_key` queda nula. El mismo
criterio aplica a las altas que entran por compatibilidad.

La clave `legacy-vitals:{id}:{metric}` hace idempotente la copia. El ID canónico
también es determinista y `batch_id` conserva el ID de la toma. La migración
comprueba las unidades del catálogo y verifica en SQL que cada valor original
tenga su equivalente exacto antes del renombre. La vista reconstruye los
valores desde `clinical_measurements` y usa `vital_signs_legacy` para la
identidad y fechas, incluidas las tomas con todos sus valores nulos. Las
escrituras compatibles guardan valores en el modelo canónico y solo
identidad y fechas en las cabeceras nuevas de la tabla renombrada. Se mantiene
el mapeo EF con el nombre original para que funcione el mismo DML.

El `Down` materializa en la tabla las filas vigentes de la vista, incluidas las
altas y ediciones hechas después del `Up`. Después elimina exclusivamente las
filas con la procedencia y clave reservadas para el puente y restaura el nombre
y permisos originales. La copia bloquea temporalmente escrituras legacy y el
corte de nombres requiere un bloqueo exclusivo. La ventana de producción
depende del volumen real. El índice del paciente y fecha en la tabla
renombrada y el índice de `batch_id` en la canónica sirven la proyección
por paciente. El plan de ejecución de QA cubre ambos.

En PostgreSQL 16 desechable, la prueba sembró 3.843 tomas y reconcilió
5.769 valores, todos con diferencia cero. Comparó la forma completa de la
vista con la tabla anterior, incluidas las fechas y tomas vacías, y repitió
`Down` y `Up` después de altas y ediciones por compatibilidad.
La migración se verifica con el filtro `FullyQualifiedName~VitaFinal_` de
`CoppAddresd.IntegrationTests` y `COP_TEST_DB_CONNECTION` aislada.

### 4.7 Procedencia en app y ERP

`GET /api/v1/me/measurements` incluye los campos por elemento de `items`.
`GET /api/v1/patients/{id}/measurements` los incluye por elemento de la
lista plana. Se mantienen autenticación, alcance del paciente, orden y
paginación. Los campos son aditivos y aceptan `null`.

| Campo JSON | Contenido |
| --- | --- |
| `sourceChannel` | Canal registrado, como `vita_voice`, `vita_whatsapp`, `vita_chat` o `professional`. |
| `reportedBy` | Actor registrado, como `patient`, `professional` o `device`. |
| `status` | Estado de lectura, como `confirmed`, `pending_confirmation` o `superseded`, según las filas visibles en cada ruta. |

Una captura de voz confirmada lleva `source: "patient"`,
`sourceChannel: "vita_voice"`, `reportedBy: "patient"` y
`status: "confirmed"`. Un registro profesional conserva
`source: "professional"` y su actor registrado. Los registros históricos
sin canal o actor devuelven esos campos como `null`.

Cuando se confirma una corrección, la app conserva la observación anterior
con `status: "superseded"` y muestra la nueva como `confirmed`. El ERP
devuelve la medición confirmada vigente. Las dos filas se conservan en
el historial clínico y una propuesta pendiente no reemplaza la anterior.

### 4.8 Analítica de la captura

Una confirmación de medición que crea el recibo publica
`BiometriaMeasuredEvent` en `IBiometriaMetricsQueue` después del commit,
igual que la tarea `vitals` de la app. La fecha usa la zona del paciente; el
evento lleva los campos demográficos que espera el procesador. La captura
no suministra IMC, grasa, glucosa, cintura o cadera. El rollup existente
registra `glucosa_distribution / Sin dato` y el conteo por ciudad cuando
está disponible. El replay solo recupera el recibo y no publica otro
evento. Comida y check-in usan los eventos del programa.

La cola de biometría es en memoria. El recibo evita duplicados en
reintentos HTTP, pero una caída del proceso entre el commit y el consumo
puede perder la actualización analítica.

## Cambio en módulo tele

`tele.clinical_encounters` + columna:

| Campo          | Tipo  | Notas                                 |
| -------------- | ----- | ------------------------------------- |
| `encounter_id` | uuid? | FK → `app.encounters.id`, **SetNull** |

- La entidad `ClinicalEncounter` del módulo tele gana `public Guid? EncounterId { get; set; }`
  y navegación `public Encounter? Encounter { get; set; }` (tipo del core).
- ⚠️ El módulo tele actualmente **no referencia otros proyectos**. Esta navegación requiere
  agregar referencia de proyecto a `CoppAddresd.Domain`. **Decisión pendiente de validación en
  apply**: si la referencia rompe el aislamiento, la alternativa es dejar `EncounterId` como
  `Guid?` sin navegación (FK por convención en configuración).

## Migraciones

| Contexto                       | Historial                      | Contenido                                                                     |
| ------------------------------ | ------------------------------ | ----------------------------------------------------------------------------- |
| `AppDbContext` (core)          | `public.__EFMigrationsHistory` | crear 5 tablas (schema `app`)                                                 |
| `TelemedicineDbContext` (tele) | `tele.__ef_migrations_history` | `ALTER TABLE tele.clinical_encounters ADD COLUMN encounter_id uuid NULL` + FK |

## Seed (catálogo)

Doble vía, ambas idempotentes:

1. **Migración `AddServerCatalogSeeds`** (`public.__EFMigrationsHistory`): recurso embebido
   `Migrations/Seed/AddServerCatalogs.sql` (generado por
   `scripts/generate_server_catalogs_seed.py`, no editar a mano). Siembra unidades, métricas,
   rangos, alérgenos, ICD-10, medicamentos, organización `medicare` y aseguradoras al correr
   `dotnet ef database update` — garantiza el catálogo en el servidor y en clones sin arrancar
   la API. `ON CONFLICT DO NOTHING` (los rangos usan guard `WHERE NOT EXISTS`, no tienen clave única).
2. **`ClinicalMeasurementsSeeder`** (`IHostedService` en `CoppAddresd.Api/Seeders`, como
   `AgentCatalogSeeder`): quedó como defensa en profundidad; al arrancar detecta el catálogo ya
   sembrado y no duplica (idempotente por `code`).

### Unidades iniciales

| code    | name                          | symbol |
| ------- | ----------------------------- | ------ |
| `mg_dl` | Miligramos por decilitro      | mg/dL  |
| `kg`    | Kilogramos                    | kg     |
| `cm`    | Centímetros                   | cm     |
| `mmhg`  | Milímetros de mercurio        | mmHg   |
| `bpm`   | Latidos por minuto            | bpm    |
| `pct`   | Porcentaje                    | %      |
| `kg_m2` | Kilogramos por metro cuadrado | kg/m²  |

### Métricas iniciales

| code              | name                         | category  | unidad |
| ----------------- | ---------------------------- | --------- | ------ |
| `glucose_fasting` | Glucosa en ayunas            | metabolic | mg/dL  |
| `weight`          | Peso                         | body_comp | kg     |
| `height`          | Talla                        | body_comp | cm     |
| `systolic_bp`     | Presión arterial sistólica   | vital     | mmHg   |
| `diastolic_bp`    | Presión arterial diastólica  | vital     | mmHg   |
| `heart_rate`      | Frecuencia cardíaca          | vital     | bpm    |
| `bmi`             | Índice de masa corporal      | body_comp | kg/m²  |
| `body_fat`        | Porcentaje de grasa corporal | body_comp | %      |

### Rangos de referencia iniciales (referencia ADA/OMS, a validar clínicamente)

| métrica           | edad  | género | min  | max  | prioridad |
| ----------------- | ----- | ------ | ---- | ---- | --------- |
| `glucose_fasting` | todos | todos  | 70   | 99   | 0         |
| `systolic_bp`     | todos | todos  | 90   | 120  | 0         |
| `diastolic_bp`    | todos | todos  | 60   | 80   | 0         |
| `bmi`             | todos | todos  | 18.5 | 24.9 | 0         |
| `heart_rate`      | todos | todos  | 60   | 100  | 0         |

## Fuera de alcance (fases futuras)

- DTOs + endpoints (registrar/consultar mediciones y encounters)
- Conexión de los tests de `antares-paciente` (métricas `test_score`)
- Frontend `coppaddresd-front` / `antares-paciente`

## Riesgos

- **Frontera tele/core**: la FK debe ir en migración del tele. No cruzar historiales.
- **Referencia de proyecto tele → Domain**: validar que no rompa el aislamiento del módulo.
  Fallback: `EncounterId` sin navegación.
- **Volumen de monitoreo continuo**: mediciones puntuales por ahora; series temporales agregadas
  serían una fase posterior (vista derivada, no duplicada).
