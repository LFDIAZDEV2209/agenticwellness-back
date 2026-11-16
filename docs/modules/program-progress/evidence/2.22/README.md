# 2.22-back — contrato de uso read-only de rutinas

GET /api/v1/program/routines/{id}/usage, Wellness.View + alcance de pacientes.
Respuesta: routineId, patientCount, enrollmentCount, activeWeekCount. Estado Active
de paciente/inscripción/semana, sin eliminados; rutina efectiva en al menos un día.
Descanso, snapshot, plantilla semanal/genérica y legacy se resuelven con el código
canónico. Conteos distintos; sin datos personales ni historia completada.

Build aprobado; 2803 unitarios/105 omitidos, seis PostgreSQL aprobadas (cuatro de
uso y dos regresiones 2.11). unit-focused corresponde a dos casos incluidos en
el total, no se suma. tests-summary conserva contadores TRX. HTTP con JWT/actor
sintéticos y controller/filter/MediatR/guard/store/resolver reales: seis GET,
401/403/404 y 200 de profesional/Admin/sin perfil clínico. La conexión del host
fue forzada READ ONLY; snapshots iguales antes/después. No es login Auth real ni
smoke del nuevo endpoint en 5122. El host de prueba se detuvo al finalizar.

volume.json: 1000 semanas, cinco SELECT, 247.75 ms; active-weeks-explain.json y
query-plan-summary: 140.64 ms de ejecución, índices de estado de semanas/pacientes,
inscripción activa y PK de plantilla. Alcance SQL antes de materializar; cero
tracking, sin consultas dentro del bucle ni lectura de task_completions. No hay
métrica/rollup de dashboard: es un agregado exacto puntual para el editor.

postgres-first conserva la primera corrida 5/6: falló la comprobación del test
antes de arrancar HTTP al usar GetRawText dentro de una proyección EF. Se pasó
a leer JSON y compararlo después de materializar; la repetición completa 6/6 pasa.
build-initial conserva el error inicial de using del host de tests, corregido.
No se descartaron fallos para fabricar un gate verde.

No modifica DTO de rutina, sesión completada, detail_snapshot ni consultas 2.11.
Las regresiones existentes verifican captura/edición/replay y migración/reversa
protegida con 10001 completaciones en BD aislada. API compartida permanece e0a9c29,
sin reiniciar: ruta nueva implementada/probada en fuente, pendiente de integración
al runtime. Sin push, publicación ni tasks.md. La reparación 1.14 está en commit
separado d64a4a6, con cero global verificado y reversa.

La validación API usa el harness HTTP del repositorio. Postman CLI 1.76.0 no
admite el comando status indicado por la skill (unknown command); no se usó
ese diagnóstico como prueba del endpoint ni se publicaron resultados externos.

Contrato fuente: docs/modules/program-progress/ROUTINE-USAGE-2.22.md.
Metadata no secreta compartida: .ai/operations/organizar-experiencia-bienestar/2.22-back-contract.json.
Manifiesto sobre texto UTF-8/LF; source.json fija hashes del parche probado.
