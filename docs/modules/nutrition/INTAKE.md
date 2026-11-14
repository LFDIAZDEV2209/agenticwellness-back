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
