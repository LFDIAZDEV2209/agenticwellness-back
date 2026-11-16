# 2.22-back — uso de una rutina en semanas activas

Contrato para front:

```http
GET /api/v1/program/routines/{id:guid}/usage
Authorization: Bearer <access token>
```

Respuesta 200, sin paginación porque es un agregado escalar:

```ts
type RoutineUsage = {
  routineId: string;
  patientCount: number;
  enrollmentCount: number;
  activeWeekCount: number;
};
```

No hay valores de ejemplo presentados como uso real. Los contadores se calculan
desde la BD; no se derivan de la página del catálogo ni de pacientes cargados por front.
No se añadió `usageCount` al DTO existente de rutina ni se cambió su contrato.

## Permiso y alcance

Requiere autenticación y **`Wellness.View`**, el mismo permiso de lectura del
catálogo de rutinas. `RequireContextPermission` admite permiso global o efectivo
en el contexto activo. El handler vuelve a comprobarlo con `IPatientScopeGuard`.
Admin no exceptúa el permiso; solo el filtro de asignación de pacientes.

Profesional: solo pacientes con asignación `patient_professionals` Active al
profesional derivado de la identidad, nunca de parámetros del cliente. Admin:
todos los pacientes visibles. Actor con permiso de catálogo pero sin perfil
clínico: cero en su alcance. No se retornan IDs de pacientes/inscripciones.

401 sin token válido; 403 sin permiso; 404 si el UUID de rutina no existe.
Rutina existente sin uso, o archivada/Draft sin rutina efectiva vigente: 200 con
ceros. Los ceros están **acotados al actor**, no acreditan ausencia global de uso.
Front no debe presentar un conteo acotado como total global ni usarlo como un
bloqueo transaccional: otra petición puede cambiar una asignación después de leerlo.

## Qué se cuenta

- Paciente no eliminado, estado `Activo`; inscripción `Active`; semana `Active`.
- Se evalúa el intervalo guardado de esa semana, recortado a la duración canónica
  de la inscripción. Es una consulta por **estado de semana**, no solo por fecha
  de hoy. Se incluyen inscripciones activas de plantillas archivadas.
- La rutina debe ser efectiva al menos un día de esa semana. Se usa
  `EffectiveDayContentResolver`/`EffectiveRoutineResolver`: descanso explícito
  terminal, snapshot válido, plantilla semanal, plantilla genérica y asignación
  legacy vigente. Solo rutinas Active se consideran efectivas.
- Cada paciente/inscripción/semana cuenta una vez, aunque aparezca en siete días.
  No se cuentan semanas Locked/Completed, inscripciones PendingAssignment,
  Paused/Completed/Withdrawn ni pacientes inactivos/eliminados.

No cuenta ejercicios completados, reproducciones, XP ni simples referencias que
el resolver descarta. No genera, asigna, materializa, publica ni guarda nada.

## Persistencia, consistencia y 2.11

Lectura `AsNoTracking`, permiso resuelto antes de abrir una transacción
**REPEATABLE READ / READ ONLY**, token de cancelación propagado. Hasta cinco
SELECT por conjuntos: existencia, semanas visibles con cabeceras mínimas, filas
de plantilla ejercicio, IDs de rutinas Active y asignaciones legacy pertinentes.
No hay consultas dentro del recorrido de semanas/pacientes y no se cargan
prescripciones, menús ni historia clínica. Cero filas visibles termina antes.

La selección por alcance se ejecuta en SQL antes de materializar. Se evalúa
CQRS/pre-agregación: este conteo puntual por recurso sirve al editor, no es un KPI
de dashboard. Se mantiene exacto y sensible al alcance actual, sin rollup ni caché
de permisos que pueda servir un total ajeno. La evidencia mide 1000 semanas,
cinco SELECT y EXPLAIN de la selección; no se afirma una prueba de millones.

**2.11 permanece intacto:** no se consulta ni modifica
`task_completions.detail_snapshot`; editar el catálogo no reescribe la sesión
capturada. Se ejecutan también sus regresiones de captura, edición/replay y
reversa protegida con 10001 completaciones en PostgreSQL aislado.

## Entrega y verificación

Evidencia en `revision-2.22-back`, con copia versionada bajo `evidence/2.22/`.
Pruebas HTTP usan JWT/actor sintéticos en host MVC aislado, con autorización
real del endpoint, MediatR, scope/store/resolver reales y BD forzada READ ONLY.
No constituyen login real contra Auth ni despliegue del endpoint a API 5122.
La API compartida continúa e0a9c29; no se reinician servicios para cargar este cambio.
Front puede consumir este contrato al integrar el commit `[2.22-back]`.

Resultado: build 0 errores; **2803 unitarios aprobados/105 omitidos** (incluye dos
casos nuevos de permiso/cancelación); **6/6 PostgreSQL** (cuatro casos nuevos,
dos regresiones 2.11). El host HTTP hizo seis GET y verificó 401/403/404 y tres
200 por alcance. Volumen: 1000 semanas/pacientes, cinco SELECT, **247,75 ms** en
esta ejecución; EXPLAIN de semanas **140,64 ms**, con índices de estado de semanas,
estado de pacientes, inscripción activa por paciente y PK de plantilla.
