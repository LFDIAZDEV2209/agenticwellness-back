# Cierre de programa — 4.2

El programa de doce semanas dura **83 días**: la última semana tiene seis días.
Al completar esos seis días perfectos, la semana y la inscripción pasan a
`Completed` en la misma transacción de la última tarea. No se exige un día 84.
Las semanas de nuevas inscripciones y activaciones por fallback conservan ese
límite. Las filas históricas de siete días no habilitan una tarea en el día 84.
El reintento de la última tarea devuelve su resultado `Replay`, aunque la
inscripción ya esté completada, sin insertar otra tarea ni conceder XP de nuevo.
El response de replay mantiene los puntos originales, según el contrato anterior.

## Cierre por calendario

Una semana imperfecta tampoco impide terminar el programa. El job nocturno
existente `ReconcileStreakJob` llama a `CompleteExpiredProgramsAsync` después de
reconciliar la racha. Su siguiente pasada cierra inscripciones Active de doce
semanas cuando **hoy local es posterior a inicio + 82 días**. La transición por
calendario usa el instante de la pasada como `completedAt`; no afirma que el
paciente haya completado tareas ausentes. La transición por seis días perfectos
es inmediata al registrar la última tarea. No hay escritura desde un GET.

El reconciliador se programa con la configuración existente
`Program:Reconciliation` (por defecto activo a las 03:00 UTC); al deshabilitarlo
también se detiene su cierre por calendario. No se añade otro job, cola o flag.
Los programas Paused/PendingAssignment/Withdrawn/Completed y las plantillas
legadas de 83 semanas quedan fuera del lote por fecha.

Una sentencia PostgreSQL con CTE actualiza semanas e inscripción atómicamente,
por lotes de hasta 500. Selecciona y bloquea solo inscripciones vencidas mediante
`FOR UPDATE OF e SKIP LOCKED`, compatible con el lock de completar tareas. Dos
pasadas concurrentes no cierran la misma inscripción dos veces; un fallo o una
cancelación revierte la sentencia. No hace llamadas externas ni consultas dentro
de un loop. Reutiliza los índices de estado/plantilla y enrollment de semanas.

## Resumen para APP 4.10

`GET /api/v1/program/me/completion` requiere autenticación y obtiene el paciente
del JWT, sin recibir IDs. No exige permisos ERP ni una inscripción activa.
Sin perfil: 404. Sin programa terminado: **200 `{ "completion": null }`**.
Con programa terminado devuelve:

```json
{
  "completion": {
    "enrollmentId": "uuid",
    "templateName": "Programa COPP-ADRESD",
    "timezone": "America/Bogota",
    "startLocalDate": "2026-08-24",
    "endLocalDate": "2026-11-14",
    "completedAt": "2026-11-15T05:00:00Z",
    "totalDays": 83,
    "completedWeeks": 12,
    "daysWithActivity": 60,
    "perfectDays": 40,
    "tasksCompleted": 200,
    "xpEarned": 18000,
    "longestStreak": 15,
    "canStartNewProgram": true
  }
}
```

El resumen consulta datos persistidos de la última inscripción completada;
no genera narrativa ni usa IA. Actividad, tareas y días perfectos se limitan al
intervalo real. XP suma solo entradas del libro de esa inscripción que no estén
pendientes de validación. Son agregados de consulta, no una nueva captura clínica
inmutable. No expone notas internas ni otros pacientes. Se resuelve con tres
consultas por conjuntos, sin materializar las colecciones del historial completo.

`canStartNewProgram` es true cuando no existe otra inscripción Active, Paused o
PendingAssignment. Para el botón **«Iniciar programa nuevo»**, APP 4.10 reutiliza
el POST existente `/api/v1/program/enrollments/me`, con timezone y la fecha local
de inicio elegida (`startLocalDate`; si se omite conserva el lunes actual del
contrato anterior). El paciente siempre se obtiene del JWT. El perfil se bloquea
al crear la inscripción y una solicitud concurrente con otra activa/pendiente
se rechaza con 409. Tras crear la nueva, el resumen previo sigue disponible pero
su `canStartNewProgram` pasa a false; el historial y XP de la inscripción anterior
se conservan. La pantalla/botón pertenecen a la tarea APP 4.10.

Ninguna lectura, cierre, replay de batería inicial o reconciliación crea la
siguiente inscripción. El handler de batería y la recuperación omiten pacientes
con programa completado: iniciar otro requiere la acción manual. El fallback
solo activa una inscripción pendiente que ya exista; no crea otra.

No hay migraciones ni cambios de configuración del stack para esta entrega.

## Evidencia de 4.2

Build de solución: **0 errores**, advertencias existentes fuera del cambio.
Suite unitaria: **2647 aprobados, 105 omitidos** opt-in existentes. PostgreSQL:
**21/21**, sin omisiones, en bases aisladas creadas/eliminadas por los fixtures.
El filtro incluye los cuatro casos nuevos de cierre, la protección contra
reinscripción desde batería y regresiones de asignación/activación de semanas.
Verifica seis días finales, día 84 rechazado, zona horaria al cruzar medianoche,
resumen e inicio manual, reintento, dos pasadas concurrentes, cancelación y
rollback completo ante un fallo al sellar la inscripción. No se llamó a un LLM.

```powershell
dotnet build --no-restore -p:OutputPath=bin/Validation42/
dotnet test tests/CoppAddresd.UnitTests --no-build -p:OutputPath=bin/Validation42/
# Con COP_TEST_DB_CONNECTION local, resuelta solo en memoria:
dotnet test tests/CoppAddresd.IntegrationTests --no-build -p:OutputPath=bin/Validation42/ --filter 'FullyQualifiedName~ProgramaCierre_|FullyQualifiedName~ProgramAssignmentIntegrationTests|FullyQualifiedName~ActivacionPor'
```

EXPLAIN ANALYZE del SELECT de candidatos, sin UPDATE ni locks y en transacción
READ ONLY local: 1,636 ms de planificación, 2,673 ms de ejecución, 127 candidatos.
El plan local usa PK_patient_profiles y recorridos secuenciales de tablas pequeñas;
existen índices por estado/plantilla/paciente y por enrollment en las tablas de
resumen. Son una muestra de desarrollo, no percentiles ni prueba de carga.
Al integrar F4, el job podrá cerrar también esas inscripciones Active ya vencidas;
esta validación no las modificó. La integración de la pantalla APP 4.10 debe
consumir el contrato anterior. F3 y sus servicios siguen independientes.
