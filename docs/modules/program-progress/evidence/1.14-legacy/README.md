# 1.14 — reparación de referencias de default-83w

Corte final 2026-11-16T02:57:17Z: **1.10b global 0**, sin excluir pacientes.
116 inscripciones/58.825 días de la archivada y 186/1.128 de la activa sin hueco.
Solo seis media_id cambiaron; se conservaron domingo válido, 83 semanas, estado
Archived, versión, 116 inscripciones y 9.628 semanas/snapshots. Datos cerrados,
individuales, fixture 0c487f75 y sentinelas 3.20/R7 con hashes iguales.

Secuencia verificable: planning → simulation-input → simulation (resolver real,
READ ONLY, 50.322→0, 812 días iniciados sin cambios) → template-backup/backup →
apply-first (6 UPDATE) → verify-first → audit-after-first (0) → apply-idempotent
(0 UPDATE) → reverse (6 UPDATE) → verify-reversed → audit-reversed (50.322) →
apply-final (6 UPDATE) → verify-final → audit-final (0). No migración de usuarios.

simulation.json enlaza SHA-256 de la propuesta; el respaldo se creó después,
contrastando nuevamente estado/protecciones y enlazando esa simulación. Cada apply
compara cabecera, filas y hashes antes de escribir. Reversa no sobrescribe cambios
concurrentes. Los datos completos respaldados son solo siete filas de plantilla,
no historias clínicas. Credenciales/conexión permanecen en memoria/config local.

audio-full-decode.json: los 13 objetos actuales decodificados completamente con
FFmpeg, cero errores; solo HEAD/GET S3. Los reemplazos usan Ep. 1–6 de la plantilla
activa; el domingo conserva Ep. 14. C1 editorial sigue desviado: esta plantilla
repite siete audios semanalmente; el catálogo tiene 13 reales, 24 MP3 pendientes
de 37 episodios, frente a la base original solicitada de 42 publicados.

Se compiló la herramienta y solución; no hay código de producto nuevo en esta
reparación. La prueba de datos es el ciclo real apply/reversa y las cuatro
consultas canónicas, no una simulación de HTTP ni un conteo SQL aproximado.
Runtime compartido e0a9c29 sin reinicio. Sin publicación, push ni tasks.md.
Fuente contractual: REPAIR-1.14-LEGACY.md. Manifiesto sobre UTF-8/LF; respaldo
original íntegro y SHA-256 en cada reporte. Contexto fuente en source.json.
