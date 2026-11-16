# Evidencia de 1.14 — opción D

Autorización explícita de Luis: plantilla activa de 83 días, arranque A2/F2 y
consulta global, conservando datos cerrados. API compartida sigue e0a9c29;
no se reinició, publicó ni hizo push. El parche se valida en fuente/BD aislada.

Secuencia: simulation.json → template-backup.json → apply-first.json →
verify-first.json → reverse.json → audit-reversed.json → apply-final.json →
verify-final.json → audit-final.json. verify-readonly-final.json confirma de nuevo
las referencias y los sentinelas con transacción de lectura.
El respaldo se creó antes del primer apply; SHA-256 en cada reporte. La reversa
compara/restaura las siete filas originales y la reaplicación deja 83 específicas
más siete genéricas. Los guardas de cabeceras, otras plantillas, filas no podcast,
cierres y fixture protegida coinciden. Todos los informes contienen metadata no
secreta; no hay contraseña, token, cadena de conexión ni audio adjunto.

catalog-candidates.json identifica el catálogo considerado; template-backup.json
incluye HEAD/firma/tamaño/hash de prefijo; audio-full-decode.json contiene los
13 hashes completos y exit 0 de decodificación FFmpeg. Solo HEAD/GET S3.
calendar-verification.json comprueba los siete posibles días de inicio, 83 fechas
cada uno, sin huecos ni referencias ajenas a los 13. La cadencia editorial de 13
no es una garantía de distancia cronológica para cualquier inicio (mínimo 5).
C1 pide una secuencia semanal con base de 42 publicados, no 42 días entre repeticiones.
El texto c1Exception del respaldo original es impreciso en ese punto y se conserva
íntegro; manda el contrato REPAIR-1.14-D.md y el generador corregido.

1.10b no excluye pacientes: plantilla activa 0; residual global 50.322, todos de
podcast en default-83w archivada. La reversa devolvió 51.019, diferencia 697.
La captura incluye 302 activos; las 300 inscripciones/50.960 del diagnóstico anterior
son otro corte. La fixture 0c487f75 queda con 0 efectivos sin cambiar sus filas.

simulation-all-weeks-aborted conserva el primer plan: el CAS abortó sin cambios
al variar el hash de todas las semanas del stack vivo; la protección de datos
cerrados se mantenía intacta. Después se usó un nuevo respaldo con sentinelas
acotados a cierres. No se fuerza una sobrescritura del estado concurrente.

Validación: build-final.log, unit-final.log/TRX y postgres-tests.log/TRX. Se usa
el mismo filtro de tres clases PostgreSQL de 4.12; incluye los dos casos A2/F2 nuevos.
La corrida de 159 terminó 158 aprobados/1 fallido. Se ajustó el timestamp sintético
de la completación dentro de ese único test (sin cambio de producto) y se repitió
con resultado 1/1 aprobado. Los 159 escenarios quedan verificados mediante ambas
ejecuciones; no se afirma una única corrida 159/159 exitosa. Ambos TRX se conservan.
Los resultados desglosados están en tests-summary.json. tested-source.json
fija los hashes LF del parche probado, sobre base 6a3470a; el commit de entrega
puede contener además documentación/evidencia posterior a la compilación.

La ejecución ampliada previa se conserva como postgres-before-fixture-clock-fix:
199 aprobados, 12 fallidos y 1 omitido (212 total). Las fixtures de IA mezclaban
CreatedAt real con capturas fijas del 15-11 y perdieron visibilidad después de
23:00 UTC; se fijaron fechas de inscripción/ingestas. Otro caso legacy sin programa
publicaba en UTC pero pedía estado Bogotá: se alineó la zona de su fixture/lectura
con ese fallback. No se modificó la política legacy de zonas en producto.
La repetición final acredita los escenarios con fechas coherentes y no presenta
la corrida anterior como aprobada ni afirma haber repetido los 212 casos.

build-initial.log conserva un primer error de compilación al escribir la aserción
antes que su helper de test; se completó el helper y los builds finales pasan.
Una ejecución unitaria y la instalación del decodificador se interrumpieron por
falta de disco. Se liberaron únicamente outputs generados de Validation114 con
dotnet clean; la repetición final pasó. No se borraron fuentes, evidencia ni servicios.

Fuente contractual: docs/modules/program-progress/REPAIR-1.14-D.md.
Origen compartido: openspec/changes/organizar-experiencia-bienestar/revision-1.14-d.
manifest.json cubre los archivos versionados tras normalizar texto a UTF-8/LF.
