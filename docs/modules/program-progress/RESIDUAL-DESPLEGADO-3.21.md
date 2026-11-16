# 3.21 — residual desplegado y reparación preparada

El cero local de [VERIFICACION-3.21](VERIFICACION-3.21.md) **no acredita cero en RDS**.
Desplegar código no replica las reparaciones de datos locales de 1.14.

## Estado conocido y límite de evidencia

**Fuente actual:** consulta real READ-ONLY a RDS del **16-11-2026**, vía ECS dbops,
task definition **`cooppadresd-dbops:4`**, resultado aportado por Luis durante esta
sesión. Sustituye al handoff como fuente del estado desplegado. No fue ejecutada
por este carril; aquí se conserva su transcripción estructurada y procedencia.

| Plantilla | Estado conocido |
| --- | --- |
| `program-coppaddresd-83-days` | **v4**, 12 semanas, 7 podcasts genéricos **todos válidos**, `media/audio`, sin demo; **0 específicos**. No tiene las 83 celdas específicas de la reparación local D. |
| `default-83w` | **Archived v3**, 83 semanas, **116 inscripciones Active**, **6 referencias demo**, lunes–sábado. Domingo válido `ed1e3490-1e91-41a9-ba17-611941b0dd31`. |

Catálogo desplegado: **68 Published / 34 con audio real** según esa consulta.
Los pacientes demo **1000000001** y **55551234** están en la plantilla **activa**:
no les afecta este residual legacy. Esto no demuestra ausencia de otros huecos
ni sustituye la consulta canónica 1.10b.

Esta sesión intentó refrescar esa lectura con
[`parity-3.21-readonly.sql`](../../../scripts/qa/program-assignment/parity-3.21-readonly.sql):
SSH al bastión agotó el tiempo de conexión. La consulta **no llegó a RDS**.
AWS confirmó el bastión con la misma dirección, SSM `ConnectionLost`, RDS disponible
y ECS Exec deshabilitado en la tarea API. No se cambiaron redes, permisos, tareas,
secretos ni configuración desplegada para acceder. `fya-root` expiró; las consultas
de inventario de solo lectura usaron el perfil `default` existente.

La vía dbops aportada por Luis resuelve la falta de lectura fresca para **estado
de plantillas/catálogo**, aunque este carril no dispone del JSON/log crudo ni de su
SHA-256 original. No se atribuye a esa consulta un resultado de **1.10b** que no
incluye. **No se trasladan los 50.322 días de un corte local antiguo a RDS**;
el número desplegado actual de días con hueco queda sin medir aquí.
La transcripción, procedencia y los fallos del acceso propio están en
[`deployed-observation.json`](evidence/3.21/deployed-observation.json).

## SQL preparado, sin ejecutar

- [Aplicación](../../../scripts/qa/program-assignment/default-83w-deployed-3.21-apply.sql):
  respaldo externo de siete filas completas y cabeceras, seguido de **seis UPDATE**,
  uno por weekday 1–6, exclusivamente de `media_id`.
- [Reversa](../../../scripts/qa/program-assignment/default-83w-deployed-3.21-reverse.sql):
  lee ese mismo respaldo, compara cabecera y postimagen completas y restaura
  exactamente las seis referencias anteriores. Aborta si hubo ediciones posteriores.

Los reemplazos se resuelven **en la BD de destino**, desde la fila genérica podcast
de la plantilla activa para el mismo weekday. No se copian UUID de una BD local.
La correspondencia preparada es:

| Weekday ISO | `media_id` demo legacy confirmado | Reemplazo |
| --- | --- | --- |
| 1, lunes | `e7c3979b-5dbe-4b9c-a77d-7b8014b0a355` | `media_id` genérico válido de la activa, weekday 1 |
| 2, martes | `66d8b715-bfae-4545-bbd9-ecc4715060c6` | Genérico activo weekday 2 |
| 3, miércoles | `be217da4-6b1f-4c4c-9b00-67bf940fcaf1` | Genérico activo weekday 3 |
| 4, jueves | `7a23f517-b71f-44cf-9cea-a1e2a8bcd44d` | Genérico activo weekday 4 |
| 5, viernes | `ab682ed6-f6c6-49cf-a064-c5eec53ce3fb` | Genérico activo weekday 5 |
| 6, sábado | `ec47f53d-fe77-42c1-b772-2418e39e5cf0` | Genérico activo weekday 6 |

Los seis UUID de destino no se incluyen en el dato recibido: el SQL los selecciona
por weekday, valida y respalda junto con la fila/media completos antes de escribir.
No inventa IDs ni usa el catálogo local para deducirlos.
Ambos SQL conservan domingo, duración, versión, estado, inscripciones, semanas,
snapshots, nutrición, rutinas, completaciones, XP y cierres. No crean objetos
persistentes ni alteran migraciones; solo usan una tabla temporal para el respaldo.

Guardas: Archived v3/83 semanas, activa v4/12 semanas, 116 inscripciones activas,
siete filas genéricas únicas, seis demo exactas lunes–sábado, domingo válido,
siete medios de origen Published/Podcast con clave no vacía y distinta del demo.
Bloquean cabeceras/filas en orden estable y medios de origen, usan SERIALIZABLE,
timeouts y comparación completa de preimagen/postimagen. Cualquier fallo revierte
la transacción completa. Una repetición de apply falla al no encontrar seis demo;
no se anuncia como apply idempotente.

**No se ejecutó apply, reverse ni una simulación que ejecute estos SQL**, tampoco
en local. La revisión de este borrador es estática; no se afirma reversa probada.
La reversa anterior de la reparación local Python sí fue probada, pero no valida
automáticamente estos nuevos archivos SQL.

## Secuencia para el orquestador

1. Recuperar acceso, repetir la consulta read-only y guardar su JSON/hash. Verificar
   los seis orígenes por weekday y los objetos reales de audio en S3 con HEAD/GET;
   Published/clave válida en BD no prueba por sí solo que el objeto sea audible.
2. Revisar/aprobar el SQL contra esa captura. Antes de RDS, probar aplicación/reversa
   en una copia aislada del estado observado. No aplicar sobre la BD local reparada.
3. Crear un directorio **nuevo y vacío** y usarlo como directorio actual de psql.
   `backup.csv` **no debe existir**: `\copy TO` puede sobrescribirlo. Conservar ese
   directorio como evidencia; una nueva operación requiere otro directorio.
4. Solo después de la decisión del orquestador, ejecutar con conexión segura existente
   (`PGSERVICE`/`.pgpass`, sin contraseñas en argv):

   ```powershell
   # Desde el directorio nuevo; rutas absolutas a los archivos revisados.
   psql -X -v ON_ERROR_STOP=1 -f <ruta-absoluta-apply.sql>
   Get-FileHash -Algorithm SHA256 backup.csv
   ```

   El apply exporta `backup.csv` **antes** del primer UPDATE y hace COMMIT solo al
   comprobar seis filas y la postimagen exacta. Guardar log, hash y resultado. Si
   falla, no reutilizar ni sobreescribir el respaldo; investigar y crear otro directorio.
5. Repetir 1.10b con handler/resolver reales, continuidad y sentinelas de datos
   cerrados; no declarar cero a partir de seis filas SQL corregidas. Si se decide
   revertir, usar el directorio/respaldo del apply correspondiente:

   Comprobar primero el SHA-256 de `backup.csv` contra el guardado tras apply.

   ```powershell
   psql -X -v ON_ERROR_STOP=1 -f <ruta-absoluta-reverse.sql>
   ```

La reparación propuesta no añade las 83 específicas de D ni migra inscripciones
de 83 semanas a 83 días. Tampoco declara C1 satisfecho: el antecedente local era
13 audios reales/37 episodios y base original de 42; el catálogo **desplegado**
ahora informado es 68 Published/34 con audio real. Son inventarios distintos;
no se afirman 24 MP3 pendientes en RDS ni se equipara conteo de medios con C1.
El orquestador conserva la decisión de ejecutar y el owner la aprobación del cierre.
