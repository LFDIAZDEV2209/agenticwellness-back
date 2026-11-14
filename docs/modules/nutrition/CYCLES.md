# Ciclos nutricionales ? Fase 3

## 3.1 ? Cabeceras y compatibilidad (D0013)

`app.nutrition_cycles` conserva el `cycle_id` existente como PK y el paciente
como FK. `state=borrador` exige número/inicio/fin nulos; `en_curso`,
`por_revisar`, `revisado` e `interrumpido` exigen número positivo y fechas.
`UNIQUE(patient_id, number)` admite varios borradores sin consumir números.
Las ventanas históricas de hasta 30 días se conservan; el flujo nuevo fija 30.

La migración `20261114171403_AddNutritionCycles` agrupa por ciclo y elige la
última versión históricamente aprobada (también si fue sustituida), conserva
sus fechas y numera por inicio/publicación/creación/ID dentro del paciente.
Los ciclos sin publicación histórica nacen como borradores, aunque una versión
borrador traiga fechas copiadas. Rechaza un cycle_id compartido entre pacientes.
El relleno es por conjuntos; no depende de tener exactamente cuatro ciclos en
cada entorno y no modifica prescripciones, menús ni inscripciones.

La FK de prescripción se añade NOT VALID y se valida después del backfill.
Un trigger de compatibilidad crea la cabecera al insertar una prescripción y
asigna número al primer aprobado con fecha de aprobación, sin renumerar al
crear/publicar versiones posteriores. Toma el mismo lock de paciente que el
flujo actual: publicaciones concurrentes se serializan y un rollback no consume
números. Rechaza una prescripción cuyo ciclo pertenezca a otro paciente.
Este puente permite coexistencia con escritores anteriores; no es una API de
publicación ni omite validaciones o permisos del caso de uso.

`PatientScopeStore.OwnerAsync(Cycle)` lee la cabecera canónica. Los handlers de
cualquier ruta de ciclo deben usar `IPatientScopeGuard` antes de consultar datos.
3.1 no añade endpoints ni cambia permisos.

Reversa de mantenimiento: exportar primero cabeceras/numeración. Down retira
trigger/FK/cabeceras y conserva todas las prescripciones y sus cycle_id; Up
reconstruye el backfill. Down rechaza revisiones/retiros que no se pueden derivar
de las prescripciones. Antes de revertir funcionalidades futuras se deben
conservar sus datos; no ejecutar Down automáticamente en producción.

## Evidencia 3.1

Aplicación LOCAL por SQL generado y revisado: `Get-Content -Raw -Encoding UTF8
$env:TEMP/copp-31-up.sql | docker exec -i coppAddresd psql -v ON_ERROR_STOP=1
-U app_user -d coppaddresd`. Resultado: 4 ciclos publicados, 0 borradores, 0
huérfanos/cruces de paciente; hash de todas las prescripciones idéntico antes y
después. No se conectó a producción. Reversa ejercitada únicamente en BD efímera.

Comandos de validación (todos verdes):

```powershell
dotnet build --no-restore -p:OutputPath=bin/Validation31/ -v quiet
dotnet test tests/CoppAddresd.UnitTests --no-build -p:OutputPath=bin/Validation31/ -v normal
dotnet test tests/CoppAddresd.IntegrationTests --no-build -p:OutputPath=bin/Validation31/ --filter 'FullyQualifiedName~Ciclos_|FullyQualifiedName~ActivacionPorFecha_|FullyQualifiedName~ActivacionPorSemanaPerfecta_|FullyQualifiedName~Scope_NewModuleEnforcesOwnForeignGlobal' -v normal
```

Build 0 errores; unitarios 2500 aprobados/105 omitidos; PostgreSQL 7/7,
incluidos cuatro recorridos de activación y alcance. La primera corrida unitaria
tuvo un fallo de expiración de Valkey; repetición completa verde. El test de
volumen usa 10.004 prescripciones y verifica Up/Down/Up, FK y ausencia de cambios;
el de concurrencia prueba borradores sin número, dos publicaciones serializadas,
unicidad y ciclo ajeno rechazado. No hay endpoints nuevos en esta tarea.
