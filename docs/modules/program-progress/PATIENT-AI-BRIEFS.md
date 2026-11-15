# Borradores IA del paciente — Fase 4, tarea 4.3

## Decisión y frontera

Se crea `app.patient_ai_briefs` para el resumen del paciente y las sugerencias de ajuste
semanal. D28 queda registrada en `design.md` del change compartido
`organizar-experiencia-bienestar` y se conserva aquí para su revisión junto al código.

`ProgramNarrativeProcessor` guarda ES/EN en `program_assignment_recommendations`, junto
con snapshot, propuesta y estados de asignación. La FK compuesta a inscripción/paciente,
la unicidad de recomendación vigente por inscripción y los jobs de recuperación impiden
reutilizar ese agregado añadiendo solo `kind`: un paciente sin inscripción también necesita
resumen, y una sugerencia no es una nueva asignación. Relajar esas reglas afectaría el flujo
existente. La narrativa sigue allí y no se copia. El cierre nutricional conserva su propio
`nutrition_cycle_reviews.ai_draft_json`: [AI-CYCLE-SUMMARY](../nutrition/AI-CYCLE-SUMMARY.md).

Esta tarea entrega entidad, mapeo EF y migración; no añade endpoints ni ejecuta IA.
4.4/4.5 integrarán los contratos y el transporte existente de IA (configuración,
consentimiento, correlación, resiliencia). Cada operación deberá exigir permiso IA y
alcance del paciente. Aplicar una sugerencia requerirá acción humana, marca `individual`
y bitácora en la misma operación clínica. El estado almacenado no ejecuta cambios.

## Modelo mínimo

| Columna | Tipo / regla |
| --- | --- |
| id | UUID, PK; generado por la aplicación |
| patient_id | UUID requerido, FK a `app.patient_profiles`, ON DELETE RESTRICT |
| kind | varchar(32), `patient_summary` o `weekly_plan_suggestions` |
| draft_json | jsonb requerido, objeto; respuesta estructurada según el contrato de cada tipo |
| model_name | varchar(120), nullable si el proveedor no lo informa; no vacío cuando existe |
| generated_at | timestamptz requerido, UTC; fecha de generación |
| input_hash | varchar(64), SHA-256 hexadecimal minúsculo de los bytes UTF-8 del request exacto |
| state | varchar(16), `draft` por defecto; también `applied` y `dismissed` |

Cada generación conserva una fila; paciente/tipo/hash pueden repetirse. No hay unicidad
por hash ni se presenta como idempotencia. Las entradas exactas y el orden de serialización
los fija cada futuro contrato antes del envío; el hash no se recalcula desde jsonb, cuyo
orden/espacios pueden cambiar. No se almacena otra copia del expediente ni se promete
reconstrucción del request desde el hash. Las fechas deben llegar en UTC; el modelo ausente
queda null, sin inventar una identificación del proveedor.

Se guarda el borrador, sin columna de mensaje publicado ni vínculo ficticio a inscripción.
No se añade un job de generación ni recuperación, repositorio sin consumidor, evento de
dashboard o infraestructura adicional. Se evaluó CQRS: no hay KPI ni agregación nueva.

## Consulta e índice

```sql
SELECT id, draft_json, model_name, generated_at, input_hash, state
FROM app.patient_ai_briefs
WHERE patient_id = @patient AND kind = @kind
ORDER BY generated_at DESC, id DESC
LIMIT @limit;
```

El índice `ix_patient_ai_briefs_patient_kind_generated`
`(patient_id, kind, generated_at DESC, id DESC)` da orden estable y cubre la FK por
prefijo izquierdo. No se añade índice redundante por paciente ni por estado aislado.
Un futuro handler consultará con proyección, AsNoTracking, límite y CancellationToken;
esta tarea no implementa la API de lectura ni sus reglas de selección del borrador vigente.

## Migración y reversa

`20261115174139_AddPatientAiBriefs` crea tabla, CHECKs, FK e índice. No cambia datos,
columnas ni restricciones existentes y no hace backfill. El índice normal se crea sobre
la tabla nueva vacía; no requiere CONCURRENTLY sobre una tabla con tráfico.
La aplicación anterior puede coexistir con la migración aplicada.

`Down` elimina solamente la tabla nueva. Borra sus borradores: exportarlos antes de una
reversa con datos útiles. Se puede revertir la aplicación dejando la tabla instalada.
Pruebas en una BD PostgreSQL nueva por caso, eliminada por el fixture; nunca sobre la BD
compartida. Ningún servicio necesita reiniciarse para esta validación.

## Verificación

`PatientAiBriefPostgresTests.cs` verifica escritura sin inscripción, ambos tipos, JSON
bilingüe, modelo ausente, historial repetido y orden estable por paciente. Rechaza paciente
inexistente, tipo/estado/hash/JSON/modelo inválidos y eliminación del paciente referenciado.
El test de volumen usa 4.000 filas y EXPLAIN (ANALYZE, BUFFERS) para comprobar el índice.
La prueba Down/Up conserva snapshot, propuesta, narrativas y estado de asignación;
verifica además que el modelo EF no tenga cambios sin migración.

Resultado 15-11-2026: build **0 errores**, unitarios **2647 aprobados / 105 omitidos**,
PostgreSQL **14/14 aprobados**, sin omitidos: tres casos nuevos y once de
`ProgramAssignmentIntegrationTests` (asignación, podcasts, recuperación de narrativa,
concurrencia, rollback y cierre). SQL generado revisado: solo CREATE TABLE, COMMENT,
CREATE INDEX e inserción del historial EF, dentro de una transacción; sin ALTER/UPDATE
sobre tablas existentes. No se aplicó la migración a la base compartida.

Comandos (conexión PostgreSQL local resuelta solo en memoria):

```powershell
dotnet build --no-restore -p:OutputPath=bin/Validation43/ -v quiet
dotnet test tests/CoppAddresd.UnitTests --no-build -p:OutputPath=bin/Validation43/ -v quiet
dotnet test tests/CoppAddresd.IntegrationTests --no-build -p:OutputPath=bin/Validation43/ --filter 'FullyQualifiedName~PatientAiBrief|FullyQualifiedName~ProgramAssignmentIntegrationTests' -v normal
```

No cambia el contrato HTTP; no requiere una colección o mock nuevo. El CLI Postman
instalado responde `unknown command 'status'`; no se inicializó ni publicó workspace.
La evidencia HTTP real de 3.20 y 4.10 depende del stack coordinado por el orquestador.
