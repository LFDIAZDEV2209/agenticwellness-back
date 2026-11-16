# 1.14 — residual de default-83w reparado, cero global verificado

Luis amplió el alcance el 15-11-2026 para cubrir las inscripciones activas que
todavía usan la plantilla archivada. Se eligió reparar sus referencias, conservando
la duración de 83 semanas: migrar a una plantilla de 83 días cambiaría su calendario.

## Alcance y protección

Plantilla `default-83w`, `89ca1806-fb44-4e06-87e5-b8904312ef18`, permanece
**Archived, 83 semanas, versión 3**. Se actualizó exclusivamente `media_id` en
seis filas genéricas podcast (lunes a sábado): Ep. 8–13 tenían clave demo y se
reemplazaron por Ep. 1–6, las referencias genéricas válidas de la plantilla activa.
El domingo conserva Ep. 14, que ya era válido. Los siete quedan Published con
audio real verificado. Se repitió la decodificación completa de los 13 MP3 disponibles,
con cero errores, sin subir, editar ni publicar medios.

No se modificaron inscripciones, fechas, estados, duraciones, semanas, snapshots,
individuales, rutinas, nutrición, completaciones, XP, prescripciones ni cierres.
Los hashes de cabeceras, otras plantillas, filas no podcast, las 116 inscripciones,
sus 9.628 semanas y los cierres protegidos coinciden antes/después.
La fixture `0c487f75` y los sentinelas de 3.20/R7 permanecen intactos.

La simulación confirmó **812 días de semanas iniciadas sin cambio efectivo**,
incluidos los podcasts válidos de sus snapshots antiguos sin origen. No había
completaciones ni check-ins en estas 116 inscripciones; si aparece historial
durante la operación, el script detiene el apply en lugar de sobrescribirlo.

## Simulación, respaldo y reversa ejecutados

1. Plan de seis cambios, solo lectura; referencias de origen comprobadas en BD/S3.
2. Simulación en memoria con el `EffectiveDayContentResolver` y el handler real de
   1.10b, transacción READ ONLY/REPEATABLE READ: **50.322 → 0**.
3. Respaldo JSON posterior a la simulación, enlazado con su SHA-256: siete filas
   completas antes/después, cabecera, referencias verificadas y hashes de protección.
4. Apply: **6 UPDATE** de `media_id`, bloqueo de cabecera/filas, comparación del
   estado esperado y transacción corta. S3 se verifica antes de tomar locks.
5. Verificación real: cero global. Segundo apply: **0 cambios**, idempotente.
6. Reversa: **6 UPDATE** restauran exactamente los valores originales y la consulta
   devuelve **50.322**. Verificación contra el respaldo original aprobada.
7. Reaplicación: **6 UPDATE**; verificación final contra el estado reparado aprobada.

El script solo permite conexión local, exige que la plantilla continúe Archived
con 83 semanas, no reemplaza evidencia existente y aborta ante cambios concurrentes
en referencias/sentinelas. La reversa tiene las mismas condiciones de comparación.

## 1.10b final, sin exclusiones

Captura **16-11-2026 02:57:17 UTC** (15-11, 21:57 America/New_York/Bogota).

| Alcance | Inscripciones activas | Días evaluados | Días con hueco |
| --- | ---: | ---: | ---: |
| `default-83w` archivada | 116 | 58.825 | **0** |
| `program-coppaddresd-83-days` activa | 186 | 1.128 | **0** |
| Global | 302 | 59.953 | **0** |

Podcast / ejercicio / nutrición: **0 / 0 / 0**. Se repararon **50.322 días** en
116 inscripciones. `0c487f75` permanece incluida y conserva cero efectivos.
**Residual: 0.** La causa anterior eran las seis referencias demo de la plantilla
archivada que consumía el resolver. No se filtraron pacientes para obtener el cero.

La desviación editorial C1 sigue explícita: se dispone de 13 audios reales frente
a la base original de 42; esta plantilla usa siete distintos, repetidos semanalmente.
Quedan 24 MP3 pendientes en el catálogo actual de 37. Cero huecos no acredita C1
completo, ni cierra la UI de 4.11 o la aprobación administrativa de 3.21.

## Evidencia y reproducción

[Paquete y manifiesto](evidence/1.14-legacy/README.md), origen compartido
`openspec/changes/organizar-experiencia-bienestar/revision-1.14-legacy-back`.
Herramienta `scripts/qa/program-assignment/repair-legacy-template-1.14.py`:
`plan --file <propuesta> --report <reporte>`, simulación C#
`--simulate-legacy-repair <propuesta> <simulación>`, después
`backup --from-plan <propuesta> --simulation <simulación> --file <respaldo> --report <reporte>`;
`apply`, `reverse` y `verify` usan el mismo `--file <respaldo>` y reportes distintos.
`verify --expect before` comprueba la reversa; el default comprueba el estado aplicado.

Build de la herramienta y solución aprobados. La prueba relevante es el ciclo real
de seis escrituras/reversa y la consulta canónica en cada estado; no hubo cambio de
código de producto en esta reparación. API 5122 sigue e0a9c29, sin reiniciar servicios.
Conexión/credenciales solo en memoria; metadata operativa sin secretos en
`.ai/operations/organizar-experiencia-bienestar/1.14-legacy.json`.
