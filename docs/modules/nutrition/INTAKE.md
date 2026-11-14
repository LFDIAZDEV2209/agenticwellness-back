# Registros de comida y revisión diaria

## 3.6 — Foto IA y reintentos

`POST /api/v1/program/nutrition/log` conserva el contrato anterior y acepta
`client_request_id` opcional (1–128 caracteres) en el nivel superior, junto a
`mealCode`, `localDate` e `intake`. `intake.foodAnalysisId` es el ID público del
análisis, no el ID de la fila. Paciente y actor se resuelven del JWT.

POST y PUT comprueban que el análisis exista, tenga dueño y ese dueño coincida
con el actor y con el usuario del paciente. Ajeno, anónimo o inexistente → 404
genérico, antes de modificar hábito, registro, XP o clave. El endpoint de análisis
ya exige autenticación y consentimiento (0.11d); no se aceptan fotos anónimas
antiguas como si pertenecieran al paciente.

Una clave nueva persiste la huella del contenido y la referencia al hábito en
`app.nutrition_intake_requests`, junto con registro y XP en la misma transacción.
La repetición idéntica devuelve la misma referencia con XP 0; no modifica la fila,
no emite otra métrica y no deshace una corrección realizada después por PUT.
La misma clave con otra comida, fecha o contenido → 409 CLIENT_REQUEST_ID_REUSED.
Los decimales equivalentes y source manual por defecto tienen la misma huella;
una fecha omitida sigue siendo omitida en ella, sin cambiar al repetir al día siguiente.

El lock de paciente precede al de inscripción, consistente con publicar/retirar.
Dos solicitudes concurrentes con la misma clave dejan una sola operación. El
fallo al guardar la clave revierte también hábito, registro y XP; se restaura el
tracking de la operación para poder reintentar en el mismo contexto.

Las claves se separan del registro porque hidratación admite varias operaciones
con claves distintas sobre la misma fila. El total sigue siendo monótono; repetir
un tap anterior no reduce el total ni vuelve a otorgar XP. Sin clave se mantiene
el contrato anterior: comida repetida 409, agua acumulable. PUT no cambia la clave
ni la huella del alta. No se guarda un historial de peticiones con datos clínicos:
solo la huella, la referencia y la fecha.

Migración aditiva `20261114184553_AddNutritionIntakeRequests`, sin backfill ni
cambios de registros previos. Down de mantenimiento exige conservar las claves
si la tabla ya tiene filas, para no perder la garantía de reintento.

Evidencia 3.6: build verde, unitarios **2510 aprobados/105 omitidos**, PostgreSQL
`NutritionPlanWorkflowTests` completa **109/109**, sin omisiones/fallos (7 min14s).
Incluye F1/F2 y ambas ConservaPreedicion. Pruebas nuevas: dueño/ajeno/anónimo/
inexistente, PUT ajeno, clave reutilizada, corrección conservada, dos solicitudes
concurrentes, hidratación con varias claves, fallo al guardar la clave con
rollback/reintento en el mismo contexto, cancelación y reversa protegida.

```powershell
dotnet build --no-restore -p:OutputPath=bin/Validation36/ -v quiet
dotnet test tests/CoppAddresd.UnitTests --no-build -p:OutputPath=bin/Validation36/ -v normal
dotnet test tests/CoppAddresd.IntegrationTests --no-build -p:OutputPath=bin/Validation36/ --filter 'FullyQualifiedName~NutritionPlanWorkflowTests' -v normal
```

SQL Up/Down generado y revisado; Up aplicado SOLO en PostgreSQL LOCAL por
`Get-Content -Raw -Encoding UTF8 $env:TEMP/copp-36-up.sql | docker exec -i
coppAddresd psql -v ON_ERROR_STOP=1 -U app_user -d coppaddresd`. Tabla nueva
vacía, sin alterar registros de comida existentes; no se desplegaron servicios.
Down/Up y rechazo de Down con claves se comprobaron solo en BD efímera.

## 3.7 — Bandeja clínica por día

`GET /api/v1/nutrition/patients/{patientId}/intake?from=2026-11-01&to=2026-11-30&page=1&pageSize=30`
requiere `Nutrition.View` y `Nutrition.RecordsView`, además del alcance del paciente
asignado; ajeno → 404 antes de cargar registros o firmar fotos. Fechas obligatorias,
rango máximo 366 días, páginas de 1–100 días, orden descendente y límite en hoy de
la zona del paciente. Incluye días vacíos; una página fuera del rango queda vacía.

Respuesta: `from`, `through`, `timezone`, `page`, `pageSize`, `totalDays`,
`totalPages`, `days`. Cada día contiene `localDate`, `cycleId`, `cycleNumber`,
`continuity`, `menu` y `records`. El menú aporta `prescriptionId`, `version`,
`dayNumber` y comidas con macros planificados. Se selecciona la publicación
vigente en esa fecha local mediante el mismo calendario de 3.3/3.4, conservando
la continuidad del último día; no se aplica retrospectivamente la versión actual.

Cada registro aporta `id`, `mealCode`, macros `recorded`, `waterMl`, `origin`,
`photo`, `analysis` y `patientCorrection`. El análisis contiene su estimación
original y alimentos detectados, separados de los valores confirmados/corregidos.
Decisión de Luis: solo `ai_estimated` y `patient_confirmed`; el origen clínico
queda reservado para un futuro canal profesional verificable. Un `source` libre
enviado por la app nunca acredita autoría profesional.

Fotos solo con `Nutrition.PhotosView` y alcance, URL protegida de cinco minutos
(firma HMAC local o URL privada del almacenamiento), reutilizada por análisis en
la página. Coordinación conserva los datos permitidos por RecordsView pero recibe
`photo: null`; no se materializa su clave en la consulta. Un enlace histórico a
un análisis de otro usuario se oculta. Nunca se exponen claves, máscaras ni datos
internos del análisis. Respuesta HTTP sin caché.

Lectura en transacción Repeatable Read READ ONLY; consultas por conjuntos,
menús acotados a las versiones necesarias de la página, firmas fuera de la
transacción. No crea auditoría ni modifica contenido. Sin migración.

Validación: firma temporal local/nube, análisis original frente a corrección,
menú por fecha, coordinación sin fotos, permisos, paciente ajeno, enlace antiguo
ajeno y origen clínico falsificado. La prueba genérica de alcance distingue
parámetro y permiso para verificar ambos atributos del endpoint.

Evidencia 3.7: build verde, unitarios **2520 aprobados/105 omitidos**;
PostgreSQL **110/110**, suite `NutritionPlanWorkflowTests` completa (8 min46s),
incluidas ambas regresiones ConservaPreedicion y Fase 2.

```powershell
dotnet build --no-restore -p:OutputPath=bin/Validation37/ -v quiet
dotnet test tests/CoppAddresd.UnitTests --no-build -p:OutputPath=bin/Validation37/ -v normal
dotnet test tests/CoppAddresd.IntegrationTests --no-build -p:OutputPath=bin/Validation37/ --filter 'FullyQualifiedName~NutritionPlanWorkflowTests' -v normal
```

## 3.8a — Revisión diaria persistida

Rutas bajo `/api/v1/nutrition`:

- `POST cycles/{id}/days/{day}/review` marca la lectura (`Nutrition.Manage`).
- `DELETE cycles/{id}/days/{day}/review` elimina la marca (`Nutrition.Manage`).
- `GET cycles/{id}/days/reviews?from&to` lista hasta 100 días del tramo
  transcurrido del ciclo (`Nutrition.View` y `Nutrition.RecordsView`).
- `GET intake/unreviewed-count` devuelve `{ days }` con ambos permisos de lectura.

Las rutas por ciclo resuelven su dueño con `IPatientScopeGuard`: ajeno → 404,
Admin exceptúa asignación, nunca permiso. El contador filtra pacientes activos
asignados antes de agrupar; no devuelve IDs ni conteos ajenos. Una fecha fuera
del ciclo, posterior al día local actual o al tramo de continuidad atribuible
se rechaza. La consulta de rango devuelve `localDate`, `hasRecords`,
`reviewedAt`, `reviewedBy`, `unreviewed`, `modifiedSinceReview` por día.

`app.nutrition_intake_day_reviews` tiene PK `(patient_id, local_date)`, ciclo,
actor del JWT y hora del servidor. Marcar de nuevo actualiza la marca, no duplica
filas; desmarcar es idempotente. Puede marcarse un día sin registros: la primera
comida posterior lo reabre. No cierra el ciclo ni concede XP.

`unreviewed` se deriva: día con registros sin marca, o con cambio posterior a
ella. `modifiedSinceReview` conserva la evidencia de la marca previa. Un trigger
de PostgreSQL sella `nutrition_intake_logs.updated_at` solo ante cambios reales;
insertar también lo sella. Los registros antiguos conservan NULL y se usa
`created_at`, sin backfill masivo. No cambia `created_at` al corregir. Repetir la
clave de 3.6 o guardar datos idénticos no reabre el día. Ambos sellos usan
`clock_timestamp()`; el lock de paciente serializa marcar frente a POST/PUT y
publicación. No hay I/O externo dentro de esa transacción.

Migración `20261114193738_AddNutritionIntakeDayReviews`, aditiva, aplicada solo
a la BD local. SQL Up/Down revisado; reversa de mantenimiento bloqueada mientras
existan marcas que deben conservarse. Up/Down vacío y rechazo con datos se prueban
en PostgreSQL efímero. No elimina registros de comida.

```powershell
dotnet build --no-restore -p:OutputPath=bin/Validation38a/ -v quiet
dotnet test tests/CoppAddresd.UnitTests --no-build -p:OutputPath=bin/Validation38a/ -v normal
dotnet test tests/CoppAddresd.IntegrationTests --no-build -p:OutputPath=bin/Validation38a/ --filter 'FullyQualifiedName~NutritionPlanWorkflowTests' -v normal
```

Evidencia 3.8a: build verde, unitarios **2540 aprobados/105 omitidos**;
PostgreSQL **111/111** en 8 min59s, suite completa con Fase 1/F2 y ambas
ConservaPreedicion. La prueba nueva cubre corrección tras revisar, reintento y
guardado idéntico, marcas repetidas, desmarcado repetido, fechas inválidas,
paciente ajeno, contador propio/Admin, permiso, cancelación y reversa protegida.
Aplicación local exacta: `Get-Content -Raw -Encoding UTF8
$env:TEMP/copp-38a-up.sql | docker exec -i coppAddresd psql -v ON_ERROR_STOP=1
-U app_user -d coppaddresd` (salida: tabla nueva sin filas).
