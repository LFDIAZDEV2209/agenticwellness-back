# Demostraciones externas de ejercicios — 2.14

`exercise_reference_videos.json` contiene los cuatro videos y los alias exactos
de `antares-paciente/src/data/exerciseVideos.ts`: marcha, sentarse/levantarse,
plancha y estocada. Conserva fuente editorial y autor; no descarga ni copia videos.
La revisión de licencias pertenece a 2.15 y no se presume realizada aquí.

Requiere Python + psycopg2 y las variables PG* desde el entorno seguro. Operación
administrativa global del catálogo; no pasar credenciales por argumentos.
Primero deben estar aplicadas las migraciones de medios externos de 1.8.
Desplegar primero el backend de 2.14: incluye la salida compatible de demostraciones
externas descrita abajo. Después aplicar la semilla; no ejecutar contra una API
anterior que trate `external/*` como claves de archivo.

```text
python scripts/seed_exercise_reference_media.py --mode simulate
python scripts/seed_exercise_reference_media.py --mode apply
python scripts/seed_exercise_reference_media.py --mode apply
```

La simulación es de solo lectura. Apply crea solo los medios ausentes, como Video,
Published, source_kind=external, con external_url y claves canónicas de 1.8:

- `external/vimeo/418873416`
- `external/youtube/0FkRr45YeEQ`
- `external/youtube/cCMob-mi19U`
- `external/youtube/3U9IbwjkOfk`

Reutiliza IDs existentes por clave. No republica borradores ni archivados; informa
esos casos. Claves duplicadas o metadatos incompatibles abortan sin sobrescribir.
Ejecuciones de la misma semilla se serializan con advisory lock transaccional.

Enlaza únicamente `routine_exercises.media_id` nulo con nombres del catálogo
explícito, normalizados por tildes, mayúsculas y espacios de extremos. Nunca
reemplaza demostraciones configuradas ni amplía la coincidencia por músculo o
subcadenas: «plancha lateral», «estocadas caminando» y «zancadas» no se convierten
en los ejercicios del catálogo. Si no hay ejercicio exacto, el medio queda listo
en biblioteca y el informe lo cuenta; no inventa ejercicios ni prescripciones.

Una transacción corta confirma medios y enlaces; no toca pacientes, asignaciones,
semanas ni audios. Ante error revierte. Al repetir no crea filas ni reescribe fechas.
El JSON de salida solo contiene conteos y conflictos, sin pacientes ni credenciales.
Las pruebas PostgreSQL cubren las cuatro referencias/alias, la gramática C# de 1.8,
la correspondencia con APP, simulación, concurrencia, idempotencia y conservación
de demostraciones subidas y variantes clínicas.

El snapshot conserva el contrato de la app anterior: los medios externos publicados
viajan como `referenceVideo` con URL de iframe y atribución, y `mediaUrl=null`.
Las claves `external/*` nunca se firman como archivos ni se pasan a `<video>`.
Un medio archivado no se reproduce ni reactiva el respaldo por nombre.

Simulación local del 14-11-2026: 4 medios y 3 enlaces propuestos, cero escrituras.
La BD compartida solo tiene coincidencias de marcha y plancha; sentarse/levantarse
y estocada quedan sin ejercicio exacto. La prueba aislada verifica los cuatro
videos y todos sus alias, dos aplicaciones concurrentes y repetición sin cambios.
La aplicación local queda para después del despliegue del backend, coordinada
con el carril app que está verificando compatibilidad (2.6).
