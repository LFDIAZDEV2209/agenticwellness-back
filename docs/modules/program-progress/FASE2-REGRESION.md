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

## 2.12 — bitácora

Build final `bin/Validation212Final/` verde; unitarios completos **2495 aprobados,
105 omitidos**. PostgreSQL **12/12**: nueva bitácora (tres operaciones, actor,
preimagen/postimagen, alcance ajeno/Admin, paginación y fallo de auditoría con
rollback), edición semanal y contenido, tres límites de fecha y cuatro casos
de activación de Fase 1. Acción Update compatible con lectores anteriores y
evento identificado en metadata; no añade migraciones.

## 2.16 — rutina masiva

Build verde y unitarios completos **2497 aprobados/105 omitidos**. PostgreSQL
**6/6**: lotes reales de podcast y rutina (10 pacientes, 2 iniciados, 2 ajenos,
6 aplicados, Admin, permiso, cancelación, idempotencia y lecturas constantes) y
los cuatro recorridos de preedición/activación de Fase 1. El planificador común
conserva el contrato anterior de podcasts. Sin migraciones ni cambios locales
de pacientes fuera de las bases efímeras de pruebas.

## 1.15b — vista previa real

Build verde y unitarios completos **2498 aprobados/105 omitidos**. PostgreSQL
**6/6**, incluidos los cuatro casos de Fase 1. El lote de podcasts simula antes
de aplicar, verifica los mismos conteos (6 aplicados, 2 iniciados, 2 ajenos),
prueba solo ajenos y compara todas las semanas/timestamps y el número de filas
de auditoría antes/después: sin escrituras. La ruta preview fuerza simulación
aunque el cuerpo pida escribir. La masiva de rutinas también permanece verde.

## 1.29 y cierre de la cadena autorizada

Build final `bin/Validation129/`: 0 errores, advertencias existentes. Unitarios
completos: **2500 aprobados, 105 omitidos, 0 fallos**. PostgreSQL, clase completa
`NutritionPlanWorkflowTests`: **100/100**, sin omisiones ni fallos (10 min 32 s).
Incluye ambos ConservaPreedicion, ambas combinaciones con plantilla vigente y
los casos nuevos de captura histórica, bitácora, rutina masiva, preview y copia
de plantilla. Los cuatro recorridos de activación también se ejecutaron antes
de cada commit de esta cadena.

La copia prueba 83 podcasts y las otras clases, borrador editable, fuente activa,
otra plantilla e inscripciones intactas; también rechazo de borrador fuente,
ID inexistente y permiso ausente sin creación parcial.

2.10 queda aceptada por decisión explícita de Luis: relleno legado validado con
simulación, ejecución, idempotencia y reversa; cero huecos de ejercicio/nutrición.
Los 50.840 días con huecos de podcast locales siguen pendientes de contenido real
en 1.14 y no bloquean esta cadena. No se modificaron tasks.md ni OVERVIEW, no se
hizo push ni despliegue. La migración aditiva de 2.11 requiere aplicación antes
de desplegar el backend nuevo y solo se ejercitó en las BD efímeras de pruebas.
