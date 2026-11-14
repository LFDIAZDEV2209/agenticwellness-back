# Clasificación del origen — tarea 1.6

## Relleno y ejecución — 2.10

`--fill-legacy` activa una operación separada de la clasificación original:
copia asignaciones Active vigentes por fecha, con contenido activo y del paciente
correcto, a celdas `individual` de semanas futuras sin activación ni historial.
Usa el calendario derivado de la inscripción, puntos/orden de plantilla específica
antes que genérica, y nunca inventa puntos. No reemplaza individuales ni descansos.
Dos contenidos vigentes solapados se informan y se conservan sin elegir uno.

```text
python scripts/classify_week_snapshot_origin.py --mode simulate --fill-legacy
python scripts/classify_week_snapshot_origin.py --mode apply --fill-legacy
python scripts/classify_week_snapshot_origin.py --mode apply --fill-legacy
python scripts/classify_week_snapshot_origin.py --mode simulate --reverse --fill-legacy
python scripts/classify_week_snapshot_origin.py --mode reverse --fill-legacy
```

Cada celda copiada lleva `backfill_version:2` y `_legacy_backfill_v2`, con preimagen
y postimagen. Reversa elimina celdas nuevas o restaura las reemplazadas solo si
todavía coinciden con lo aplicado y la semana sigue siendo futura. Una edición
posterior se conserva. La reversa de v2 no retira clasificaciones v1 anteriores.
Las tablas de asignaciones antiguas nunca se modifican. CAS del snapshot y
estado/fecha/historial revalidados impiden pisar activaciones o ediciones concurrentes.

Para consultar el mismo handler de huecos 1.10b con alcance administrativo global:

```text
dotnet run --project scripts/ProgramContentAudit
```

Requiere `COP_PROGRAM_AUDIT_CONNECTION` del entorno seguro. No exponer esta
herramienta como API: la opera un administrador con credenciales de BD, en una
transacción PostgreSQL de solo lectura; imprime únicamente conteos, sin pacientes.

### Ejecución local del 14-11-2026

- Clasificación v1: simulación y aplicación, cero cambios.
- Relleno: **4906 celdas en 762 semanas**, todas `individual`; 377 solapamientos
  conservados sin cambiar la asignación clínica. Se evaluaron 8457 semanas futuras.
- Segunda aplicación: cero escrituras.
- Reversa simulada y ejecutada: 762 semanas restauradas; repetición, cero escrituras.
  La huella SHA-256 de todas las semanas coincide exactamente con la anterior.
- Reaplicación final: 762 semanas; repetición final, cero escrituras.
  Las huellas de ambas tablas antiguas permanecen idénticas antes/después.
- Consulta 1.10b antes/después: ejercicio **0**, nutrición **0**, podcast **50840**
  días con huecos. Luis aceptó cerrar el relleno validado y continuar; los podcasts
  quedan explícitamente pendientes de los audios de 1.14, sin sustituirlos por demos.

Informes sin datos clínicos en `.ai/operations/organizar-experiencia-bienestar/`:
`2.10-local-execution.json` y `2.10-content-gaps-after.json`. Prueba PostgreSQL:
copias individuales, descanso conservado, conflictos, simulación, repetición,
reversa exacta y protección de una edición posterior; las cuatro activaciones
de Fase 1 también se ejecutan antes del commit.

Ejecutar con Python + `psycopg2` y las variables estándar `PGHOST`, `PGPORT`,
`PGDATABASE`, `PGUSER`, `PGPASSWORD` resueltas desde el entorno seguro. Nunca
pasar credenciales por argumentos ni guardarlas en un archivo del workspace.

```text
python scripts/classify_week_snapshot_origin.py --mode simulate
python scripts/classify_week_snapshot_origin.py --mode simulate --reverse
```

La simulación es el modo predeterminado y usa transacciones de solo lectura.
El JSON de salida solo contiene conteos, motivos y conflictos; no pacientes ni DSN.
En local se ejecutó únicamente simulación. La ejecución de relleno de asignaciones
antiguas pertenece a 2.10; este script clasifica los ítems existentes y no inventa
puntos ni copia asignaciones. Su coincidencia con una asignación antigua se
conserva como individual (con actor o ambigua sin actor).

La aplicación explícita es `--mode apply`; la reversa, `--mode reverse`.
Antes: revisar simulación, respaldo y ventana operativa. `--batch-size` (500 por
defecto, máximo 5000) limita cada transacción; paginación por UUID, sin OFFSET y
un UPDATE de conjunto por lote. Un fallo revierte el lote actual; los previos
pueden estar confirmados. Repetir es idempotente. Un conflicto concurrente se
informa y se reevalúa en otra pasada; no se sobrescribe la edición ni activación.

Solo se tocan semanas bloqueadas, sin activación ni cierre y que empiezan después
del día local actual. Las empezadas/completadas permanecen de solo lectura.
`--today` permite fijar la lectura para pruebas; la escritura siempre vuelve a
comprobar la fecha real. Omitirlo en operación.

Se compara contra la versión de plantilla al inicio solo si está disponible:
misma versión vigente y sin edición posterior a la creación de la semana.
El catálogo no conserva revisiones históricas: si no hay prueba, se conserva
como individual y se informa el motivo. Una fila específica gana a la genérica.
JSON inválido/duplicado se informa y se deja intacto.

Cada cambio guarda `backfill_version: 1` y `_origin_backfill_v1` con la preimagen
exacta del ítem. Reversa restaura solo ítems todavía idénticos a lo aplicado,
y únicamente en semanas futuras; si hubo una edición o activación posterior,
no la deshace. No retirar estas preimágenes antes de cerrar la ventana de reversa.

## Simulación local — 13-11-2026

- Semanas totales: 11.788.
- Excluidas por estado/fecha: 3.326.
- Futuras evaluadas: 8.462, todas con snapshot vacío.
- Ítems program/individual, conflictos y semanas cambiadas: 0.
- Escrituras: **0** (conexión de solo lectura).

Prueba PostgreSQL en BD efímera: simulación sin cambios, igualdad específica y
genérica, ambiguos individuales, aplicación repetida sin cambios, reversa exacta,
reversa repetida, y conservación de ediciones posteriores y semanas no elegibles.
