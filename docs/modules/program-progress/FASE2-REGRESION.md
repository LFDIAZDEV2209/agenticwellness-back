# Regresión completa — 2.8-fix, 14-11-2026

Base: `4c683c0`, rama `org/f2-coppAddresdBack`. Sin push ni migraciones locales.
El trabajo de 2.10 quedó fuera de esta validación, conservado en stash.

## Problema y corrección

Antes del arreglo se reprodujeron los dos fallos comunicados por QA:
`ActivacionPorFecha_ConservaPreedicionFutura` y
`ActivacionPorSemanaPerfecta_ConservaPreedicionFutura` (0/2 aprobados).

La edición calcula ahora el inicio desde la inscripción:
`start_local_date + (weekNumber - 1) * 7`. Si es hoy o futuro en la zona del
paciente, permite la preedición mientras no haya activación, finalización ni
historial. No usa las fechas denormalizadas de la semana para esa decisión ni
para validar los días editables. Historial y semanas pasadas siguen protegidos.

El repositorio acepta TimeProvider opcional, con System por defecto. Los casos
de activación de Fase 1 conservan sus aserciones y fijan el reloj antes de la
preedición: su inicio histórico 2026-11-02 había quedado detrás del reloj real.
Las nuevas pruebas usan fechas de semana incorrectas en ambos sentidos y
ayer/hoy/mañana en Bogotá, incluso cuando UTC ya cambió de día.

## Validación final

- Build de la solución: 0 errores; 13 advertencias existentes.
- Unitarios completos: **2487 aprobados, 105 omitidos**, 0 fallos.
- **NutritionPlanWorkflowTests completa: 94/94**, sin omisiones ni fallos,
  6 min 22 s. No se restringió a los casos nuevos.
- Pasan los cuatro casos de activación por fecha/semana perfecta, tanto la
  conservación de preedición como la combinación con plantilla vigente.
- También se ejecutaron alcance propio/ajeno, historial/XP, idempotencia,
  concurrencia, medios, semillas y las regresiones anteriores incluidas en la clase.

Se usó PostgreSQL real en las bases efímeras del fixture. La variable
`COP_TEST_DB_CONNECTION` se resolvió en el proceso de integración desde el
entorno seguro; no se registraron credenciales.

```text
dotnet build --no-restore -p:OutputPath=bin/Validation28Fix/ -v quiet
dotnet test tests/CoppAddresd.UnitTests --no-build -p:OutputPath=bin/Validation28Fix/ -v quiet
dotnet test tests/CoppAddresd.IntegrationTests --no-build -p:OutputPath=bin/Validation28Fix/ --filter FullyQualifiedName~NutritionPlanWorkflowTests -v normal
```

El directorio de salida propio evita bloquear las DLL de los servicios locales.
No se reanuda la cadena posterior durante la reproducción del QA del orquestador.

## 2.11 — después de la liberación de QA

Build final `bin/Validation211Final/`: 0 errores. Unitarios completos: 2488
aprobados y 105 omitidos. `NutritionPlanWorkflowTests` completa: **97/97**, sin
omisiones ni fallos (7 min 45 s), incluidos ConservaPreedicion y CombinaJueves de
ambas activaciones. Captura/replay/historial/medios y migración nullable con
10001 completaciones en verde. SQL Up/Down revisado; no se aplicó la migración
a la BD compartida ni se desplegaron servicios. Columna nueva requiere migración
antes de ejecutar el backend nuevo. No hay backfill de historias anteriores.
