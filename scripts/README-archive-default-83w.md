# Archivo de default-83w — 1.16

Requiere Python + psycopg2 y variables PG* resueltas desde el entorno seguro.
Solo operadores administrativos de mantenimiento. No pasar credenciales por
argumentos ni guardarlas en el respaldo. La simulación es de solo lectura.

```text
python scripts/archive_default_83w.py --mode simulate
python scripts/archive_default_83w.py --mode apply --backup RUTA_RESPALDO.json
python scripts/archive_default_83w.py --mode simulate --reverse --backup RUTA_RESPALDO.json
python scripts/archive_default_83w.py --mode reverse --backup RUTA_RESPALDO.json
```

La carpeta del respaldo debe existir. El archivo contiene solo el catálogo de
esa plantilla antes/después, sin pacientes ni credenciales. Se crea de forma
exclusiva y durable antes de escribir. No se sobrescribe un respaldo diferente.
Conservarlo durante toda la ventana de reversa.

Solo cambia `program_templates.status` de `default-83w` a `Archived`: no cambia
versión, fechas, filas de plantilla, inscripciones, semanas, snapshots ni medios.
Las inscripciones conservan su plantilla y el resolvedor 1.9 admite su duración
legada de 581 días. Si el contenido de origen tiene huecos, archivar no los repara.
La segunda ejecución es un no-op, sin UPDATE. Una plantilla ya archivada no crea
otro respaldo ni se atribuye una modificación ajena.

La reversa restaura únicamente el estado original y solo si la fila completa
coincide con la postimagen del respaldo. Si hubo una edición posterior, falla
sin pisarla. Repetir reversa es un no-op. Cada ejecución usa una transacción
corta con bloqueo de la fila; una excepción revierte la modificación.
La salida JSON informa propuestas, escrituras y conteos, sin identificar pacientes.

Prueba PostgreSQL: simulación, apply/apply, simulate reverse, reverse/reverse,
rechazo ante edición posterior e igualdad de inscripciones/semanas antes/después.
La prueba confirma que el resolvedor sigue entregando los 581 días desde una
plantilla archivada con contenido válido.

## Ejecución local — 14-11-2026

Simulación: una propuesta, cero escrituras y 116 inscripciones conservadas.
Primera aplicación: una escritura de estado. Segunda: cero escrituras.
Los conteos y hashes de las filas completas de todas las inscripciones y semanas
son idénticos antes/después. No se ha ejecutado reversa en la BD local.
Respaldo e informe operativo fuera del repo, en
`.ai/operations/organizar-experiencia-bienestar/` del workspace:
`default-83w-archive-before-a21c71d.json` y `1.16-local-report.json`.
La reversa se probó en PostgreSQL efímero, incluida la protección ante ediciones.
