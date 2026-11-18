# Seguimiento de medicamentos — ERP y app

## Flujo implementado

El personal autorizado registra el plan; pacientes confirman tomas/omisiones y
pueden añadir productos propios, sin modificar ni satisfacer las asignaciones
con un producto diferente. Nutracéutico sigue adicional. El seguimiento semanal,
el inventario y las alertas consumen estas identidades, sin convertir nombres
libres o medicamentos del perfil antiguo en nuevas prescripciones.

ERP: `/medications` (Inicio/Pacientes), `/medications/tracking`,
`/medications/inventory`, `/medications/alerts`, `/medications/audit`.
La ficha `/medications/{patientId}` añade Semana y día, Inventario y Efectos
reportados. El contador de acceso usa una cifra legible, sin anillo.

App: Control de medicamentos conserva Hoy/Mi lista/Historial y añade
Inventario y Reportes. Perfil conserva su lugar en el dock. Inventario permite
conteo inicial, reposición, ajuste motivado y consulta de movimientos. Reportes
permite indicar síntoma, intensidad y medicamento opcional; muestra el estado y
las notas de seguimiento del equipo. Esas notas son visibles al paciente.

## Datos y atomicidad

La migración `AddMedicationMonitoring` crea ocho tablas `app`:

- `medication_supplies`: balance por paciente/tratamiento y vencimiento registrado.
- `medication_stock_movements`: movimientos append-only con revisión de suministro.
- `medication_effect_reports` y `medication_effect_followups`: reporte y seguimiento.
- `medication_monitoring_alerts` y `medication_alert_followups`: condición, responsable y cierre.
- `medication_monitoring_operations`: reserva de operación/hash para reintentos.
- `medication_audit_entries`: consultas, escrituras y exportaciones atribuibles.

Solo se añaden tablas/índices/función/triggers; no se alteran ni borran columnas
anteriores. Up/Down/Up se verificaron en PostgreSQL18 aislado, conservando los
conteos de tratamientos, dosis, eventos de toma y acceso previos. El rollback
elimina únicamente las tablas nuevas y sus datos; no es una operación recomendada
sobre un sistema publicado que ya los use.

Todas las mutaciones serializan el paciente mediante el mismo lock existente.
Confirmar una dosis y descontar existencias son atómicos. Los reintentos/no-op no
descontarán dos veces. Corregir una dosis devuelve únicamente consumos posteriores
al último conteo físico. `supply_revision` evita ambigüedad de timestamps iguales.
Las tomas anteriores al primer conteo no generan consumos retroactivos.

Un balance negativo indica consumos que exceden el inventario registrado; no se
impide guardar una toma clínica por falta de existencias informáticas. Se señala
para conciliar, sin recortarlo a cero ni fabricar stock. Los días restantes usan
la pauta activa vigente y el balance no negativo. Una pauta pausada/finalizada,
sin vigencia o sin uso diario no produce una estimación de días.

Se mantiene un único balance y vencimiento conservador por tratamiento: una
reposición conserva el vencimiento más próximo conocido hasta el siguiente
conteo/ajuste. No es contabilidad por lotes/FIFO ni inventario de bodega. Las
cantidades usan la unidad de la pauta; no hay conversiones automáticas.

## Seguimiento y alertas

La semana se consulta por rango de siete días en la zona seleccionada. Conserva
pausas y cancelaciones. Los estados son a tiempo (±60 min), tardía, omitida,
sin registro tras 60 min, pendiente y cancelada. Adherencia considera solo dosis
asignadas cuyo horario llegó, no futuras, canceladas o adicionales. Sin dosis
evaluables se muestra `—`. Los tiempos proceden del registro, no prueban ingesta.

Reglas operativas del monitor:

- Existencias para 7 días o menos; severidad alta para 4 días o menos.
- Vencimiento pasado con balance positivo.
- Dosis asignada sin registro después de 60 min.
- Adherencia de 7 días inferior al 70%, con al menos 3 dosis evaluables tras 60 min.
- Reporte de síntoma no resuelto; intensidad severa eleva la prioridad.

No representan diagnóstico ni detección de interacciones. Las alertas se guardan
con clave de deduplicación por condición. Atender asigna al usuario que actúa;
resolver exige nota y revisión. Un cierre manual no se reabre continuamente si
persiste la misma condición: se abre un nuevo episodio al desaparecer y volver.
Las condiciones desaparecidas se resuelven automáticamente conservando historial.

Los cambios de inventario, tomas, tratamientos y síntomas recalculan alertas en
la misma transacción. `MedicationMonitoringWorker` revisa el paso del tiempo cada
5 min, por páginas de 50 pacientes y ámbitos de vida cortos. Se puede desactivar con
`Medications:MonitoringWorkerEnabled=false`; el flag general sigue siendo
`Medications:Enabled`. No envía push, WhatsApp, SMS, correo ni llamadas.

## Permisos y auditoría

`Medications.View`: lectura del ámbito autorizado.
`Medications.Manage`: tratamientos, inventario y seguimiento de alertas/síntomas.
`Medications.ManageAccess`: restricciones de acceso del paciente.
`Medications.Export`: exportación, además de lectura del paciente.
`Audit.View` + `Medications.View`: bitácora específica, filtrada al ámbito clínico.
El nuevo permiso de exportación se registra en Auth; los tokens se deben renovar
para reflejar nuevas concesiones. Pacientes solo acceden a sus propios recursos.

La bitácora específica registra desde esta entrega actor, email, requestId,
recurso, acción, fecha y finalidad de exportación. No copia el cuerpo clínico.
Los triggers bloquean UPDATE/DELETE de la bitácora y de movimientos/seguimientos;
no se afirma resistencia frente al superusuario PostgreSQL. Los registros
históricos de escritura anteriores siguen en la auditoría general del ERP; no
se reconstruyen accesos antiguos inexistentes.

`GET` no crea dosis ni altera estado clínico. Las consultas protegidas sí añaden
su entrada de acceso. CSV usa el cliente autenticado y escapa fórmulas. El cliente
cancela la entrega del archivo si la vista cambia de sesión/ámbito.

## Contratos

Rutas nuevas bajo `/api/v1`:

- GET `medications/patients/{patientId}/tracking?from=&timeZone=`.
- GET `medications/inventory?patientId=&query=&page=&pageSize=`.
- GET `medications/patients/{patientId}/inventory` y `me/medications/inventory`.
- GET/POST `.../inventory/{treatmentId}/movements` (propio o paciente autorizado).
- GET/POST `me/medications/effects` y `medications/patients/{patientId}/effects`.
- POST `medications/patients/{patientId}/effects/{id}/follow-ups`.
- GET `medications/alerts?patientId=&status=&kind=&page=&pageSize=`.
- POST `medications/patients/{patientId}/alerts/{id}/follow-ups`.
- GET `medications/audit?patientId=&action=&actorId=&from=&to=&page=&pageSize=`.
- GET `medications/patients/{patientId}/export?from=&timeZone=&purpose=` (CSV semanal).

Paginados máximos 100; movimientos y reportes muestran los últimos 100. Las
correcciones/operaciones viejas conservan identidad y no dependen de estar en esa
ventana. Todos los endpoints nuevos usan Bearer/refresh/contexto habituales.
No cambia el contrato de login/perfil del build 67; ese binario no se ejecutó aquí.

## Verificación reproducible

Host `tests/CoppAddresd.Medications.E2EHost`; PostgreSQL real aislado, Auth real y
pacientes sintéticos. `e2e/medications-monitoring-real.spec.ts` en ERP requiere
`COP_MEDICATION_FIXTURE` privado y base `copp_review_medications_monitoring_*`.
Prueba primero roja por falta de navegación; caso adicional rojo 8 vs 9 unidades
tras corrección/conteo. El verde cubre app→API→PostgreSQL→ERP, revisión temporal,
permisos, inmutabilidad, reintentos, exportación y auditoría. No se usa intercepción
de APIs ni envíos externos. El reloj/refresh de QA no existe en producto.

Evidencia local `/tmp/copp-medication-monitoring`: rojo, verde, regresiones,
rollback, builds, lint/i18n, pantallas ES/EN y modo oscuro. Las fixtures, backup y
credenciales quedan fuera de Git con permisos privados.
