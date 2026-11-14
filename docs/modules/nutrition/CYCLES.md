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

## 3.2 — Fechas, cierre y retiro

La primera publicación de cada paciente inicia hoy local. Un ciclo sucesor
publicado antes del cierre comienza al día siguiente del fin anterior; uno
publicado tarde empieza hoy local. Una fecha solicitada distinta de ese inicio
se rechaza: no se crean huecos ni se retrotrae la publicación. Una nueva versión
conserva la ventana del mismo ciclo. El trigger de 3.1 asigna el número en esa
misma transacción, sin consumirlo para borradores abandonados.

`NutritionCycleCalendar.State` deriva `por_revisar` desde la medianoche local
posterior al día 30. No necesita una escritura desde GET ni un reloj del servidor
para cambiar la presentación. Estados terminales/revisado se conservan. La
continuidad termina el día anterior al inicio del sucesor; se limita a hoy y
ningún día futuro cuenta. Una pausa no desplaza fechas ni cambia esta atribución.

Retirar el programa interrumpe el ciclo publicado vigente, incluso si está en
continuidad, preservando los anteriores y otros pacientes. El cambio de
inscripción y ciclo es atómico bajo lock de paciente, sin tocar semanas, XP ni
prescripciones. `interrupted_at` nullable (migración aditiva
`20261114174950_AddNutritionCycleInterruption`) conserva el instante del retiro
para que el período no siga creciendo después; se interpreta en zona del
paciente. Down exige conservar las fechas existentes antes de retirar la columna.
No añade endpoints; conserva la autorización de las rutas actuales.

Evidencia 3.2: build verde, unitarios 2504 aprobados/105 omitidos, clase completa
PostgreSQL NutritionPlanWorkflowTests **104/104**, sin omisiones (8 min 21 s),
incluidos ambos ConservaPreedicion y las dos combinaciones de activación.

```powershell
dotnet build --no-restore -p:OutputPath=bin/Validation32Final/ -v quiet
dotnet test tests/CoppAddresd.UnitTests --no-build -p:OutputPath=bin/Validation32Final/ -v normal
dotnet test tests/CoppAddresd.IntegrationTests --no-build -p:OutputPath=bin/Validation32Final/ --filter 'FullyQualifiedName~NutritionPlanWorkflowTests' -v normal
```

SQL Up/Down revisado; Up aplicado solo en local con `Get-Content -Raw -Encoding
UTF8 $env:TEMP/copp-32-up.sql | docker exec -i coppAddresd psql -v ON_ERROR_STOP=1
-U app_user -d coppaddresd`: 4 ciclos conservados y 0 fechas de retiro inventadas.
Down/Up y rechazo de Down con retiro se probaron en BD efímera. El caso anterior
que esperaba rechazar un sucesor sin fecha se actualiza para solicitar un inicio
explícitamente solapado y conservar su rechazo 409; el nuevo inicio automático
respeta el cierre anterior. No se modificaron las regresiones de preedición.
