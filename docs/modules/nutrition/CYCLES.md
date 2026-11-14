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

## 3.3 — Atribución y adherencia por rango

`GET /api/v1/nutrition/cycles/{id}/adherence?from=2026-11-06&to=2026-11-08&page=1&pageSize=30`
exige audiencia ERP y `Nutrition.View`. API y handler ejecutan
`IPatientScopeGuard` sobre el ciclo; ajeno/inexistente → 404, permiso ausente →
403, incluso para Admin. Admin solo exceptúa la asignación. No devuelve fotos,
notas internas ni datos de otro paciente.

Devuelve cabecera, rango efectivo, adherencia (fracción, redondeada a 2 decimales),
`daysWithData`, `elapsedDays`, `interruptions`, `daysWithUnknownMenu` y días
paginados (1..100 por página, orden ascendente estable). Los agregados cubren
todo el rango efectivo, independientemente de la página. Sin from/to se usa el
inicio del ciclo hasta hoy o el día anterior al sucesor, lo que ocurra primero;
con retiro se limita además a su fecha local. Un borrador no tiene días.

El ejemplo de los días 6/7/8 se consulta precisamente con from=6 y to=8 cuando
el día 8 ya terminó: 3/3, 2/3 y sin registro producen adherencia 0,83, cobertura
2 de 3 e interrupción 1. Si el 8 es hoy local y aún no tiene registros, sigue
visible en `days`, pero `interruption=false`, `elapsedDays=2`, `interruptions=0`.
Un día en curso con registros sí suma `daysWithData` y `elapsedDays` y mantiene
su adherencia proporcional; D2 no cambia. Al cruzar la medianoche del paciente,
un día vacío pasa a interrupción y suma elapsedDays, incluso durante una pausa
o continuidad. R7-03 no cambia calendario, rango, paginación ni días históricos.
Si se consulta desde el inicio del ciclo el 5-nov, ese día también forma parte
de la cobertura, sin inventar registros. Los días sin comida no entran al
promedio, con o sin pausa; una comida durante la pausa sí cuenta. Agua no es una
comida y se conserva fuera del numerador/cobertura de comidas. Los registros de
comida sin macros (marcador de hábito) sí acreditan presencia de la comida.

Cada día incluye fecha, continuidad, comidas registradas/planificadas, fracción
del día, interrupción, versión/día del menú e IDs de sus registros. Los IDs se
obtienen por paciente y fecha, nunca por cycle_id del registro. La continuidad
usa el día final del último menú publicado del ciclo anterior. Un sucesor el
9-dic deja 5–8-dic en el ciclo anterior y recibe registros solo desde el 9-dic.
Se consultan versiones históricamente publicadas, aunque hayan sido sustituidas;
una publicación dentro del ciclo solo cambia la comparación desde su día local.

No se asigna adherencia cero por falta de menú: si un día con datos no tiene
comidas planificadas conocidas, la adherencia queda null y se cuenta en
`daysWithUnknownMenu`. Sin días con datos también es null. No se limita la
fórmula ni se redondea cada día antes del promedio.

Lectura Repeatable Read READ ONLY para que versiones, sucesor y registros
pertenezcan al mismo snapshot. Precarga por conjuntos, sin SELECT por día;
proyecta cabeceras, conteos de comidas e IDs/fechas/tipos de registro, sin cargar
alimentos, imágenes ni menús completos. Reutiliza índices existentes de
paciente/fecha y ciclo/versión. Es una lectura operacional por paciente, sin
nuevas métricas de dashboard ni rollups. No requiere migración ni backfill.

Evidencia 3.3: build 0 errores, unitarios 2506 aprobados/105 omitidos,
PostgreSQL 8/8 sin omisiones. Pruebas de 0,83/cobertura 2 de 3, pausa con comidas,
futuros excluidos, alcance ajeno/Admin/permiso, paginación vacía/última, cancelación,
versiones históricas por fecha local y continuidad sin duplicados. Incluye fechas
y retiro de 3.2 y cuatro regresiones de activación Fase 1.

```powershell
dotnet build --no-restore -p:OutputPath=bin/Validation33/ -v quiet
dotnet test tests/CoppAddresd.UnitTests --no-build -p:OutputPath=bin/Validation33/ -v normal
dotnet test tests/CoppAddresd.IntegrationTests --no-build -p:OutputPath=bin/Validation33/ --filter 'FullyQualifiedName~CicloAdherencia_|FullyQualifiedName~CicloFechas_|FullyQualifiedName~ActivacionPorFecha_|FullyQualifiedName~ActivacionPorSemanaPerfecta_' -v normal
```

## 3.4 — Continuidad en la resolución efectiva

Para inscritos, `EffectiveDayContentResolver` comparte
`NutritionPublicationTimeline` con la adherencia: primero el ciclo ya iniciado
más reciente y dentro de él la publicación vigente en esa fecha local. Al vencer
sin sucesor iniciado devuelve el último día de ese menú con source=continuity;
no repite semanas ni cambia las fechas del ciclo. Un sucesor publicado tarde
solo toma sus días desde su inicio real.

La precarga incluye versiones sustituidas cuando el mismo ciclo mantiene una
publicación, para que el pasado use su menú real. Misión y lectura móvil validan
además paciente y publicación actual del mismo ciclo antes de leer esa versión.
Un ciclo cuya publicación se retiró completamente no se revive desde historia.
Una versión/sucesora ya vigente sin el menú requerido cae al motor general;
no recupera un menú anterior del mismo ciclo. Se conserva la precedencia de
respaldo del motor/plantilla y asignaciones antiguas aprobadas.

No crea migraciones ni escribe snapshots. Los pacientes sin inscripción mantienen
el contrato previo de bienestar. La consulta 1.10b usa esta misma selección.
Auditoría LOCAL READ ONLY tras el cambio: 50.840 días con hueco de podcast,
**ejercicio 0, nutrición 0**, escrituras 0. El total global no se presenta como
cero: los podcasts siguen pendientes de los mp3 reales de 1.14 (salvedad ya
aceptada por Luis), sin rellenarlos con demo. Evidencia operativa en
`.ai/operations/organizar-experiencia-bienestar/3.4-content-gaps-local.json` del workspace.

Comando de auditoría (con conexión local comprobada y resuelta solo en memoria
mediante COP_PROGRAM_AUDIT_CONNECTION):

```powershell
dotnet run --project scripts/ProgramContentAudit/ProgramContentAudit.csproj --configuration Audit34 --no-launch-profile
```

Evidencia 3.4 y cierre: build verde; unitarios **2508 aprobados/105 omitidos**;
PostgreSQL, clase completa **NutritionPlanWorkflowTests 107/107**, sin omisiones
ni fallos (6 min 28 s). Incluye ambas ConservaPreedicion y las combinaciones por
fecha/semana perfecta. La prueba nueva verifica versión histórica, calendario,
misión, app, sucesor tardío, respaldo del motor y consulta de huecos cero en el
escenario completo. La auditoría de desarrollo anterior permanece sin escrituras.

```powershell
dotnet build --no-restore -p:OutputPath=bin/Validation34/ -v quiet
dotnet test tests/CoppAddresd.UnitTests --no-build -p:OutputPath=bin/Validation34/ -v normal
dotnet test tests/CoppAddresd.IntegrationTests --no-build -p:OutputPath=bin/Validation34/ --filter 'FullyQualifiedName~NutritionPlanWorkflowTests' -v normal
```

También pasó el filtro PostgreSQL de humo 7/7 (continuidad nueva, adherencia y
cuatro activaciones). Las omisiones unitarias son los casos opt-in de la suite;
la clase PostgreSQL completa se ejecutó con conexión local real resuelta en el
proceso, sin registrar credenciales. No se acredita ejecución en producción ni
se cambian las asignaciones antiguas o inscripciones con esta tarea.

## Verificación conjunta R7-03 / R7-14

Build verde; unitarios **2630 aprobados/105 omitidos**; clase completa PostgreSQL
**NutritionPlanWorkflowTests 119/119**, sin omisiones ni fallos (11 min 10 s),
incluidos ambos ConservaPreedicion. R7-03 verifica hoy vacío/parcial/completo,
medianoche de Bogotá, pausa, rango y paginación; R7-14 verifica nombres en ES/EN,
respuesta exacta y conservación del valor histórico almacenado. No hay migración.

```powershell
dotnet build --no-restore -p:OutputPath=bin/ValidationR7/ -v quiet
dotnet test tests/CoppAddresd.UnitTests --no-build -p:OutputPath=bin/ValidationR7/ -v normal
dotnet test tests/CoppAddresd.IntegrationTests --no-build -p:OutputPath=bin/ValidationR7/ --filter 'FullyQualifiedName~NutritionPlanWorkflowTests' -v normal
```
