# KAN-170 · Preflight de vencimiento en AWS

Este preflight convierte inventario de AWS y decisiones por recurso en una
lista de revisión. Es de solo lectura. No escribe políticas, no elimina datos
y no genera comandos de borrado. Su resultado `eligible` significa elegible
para revisión de ejecución, no una prueba de que algo se borró.
El [acuerdo de producto][kan114] y la [matriz operativa](retention-proposal.md)
son la base. No se reabren los plazos. Se revisa si corresponden a cada copia
y si existe una excepción de custodia o recuperación.

[kan114]: https://diomedescerda.atlassian.net/browse/KAN-114?focusedCommentId=10073

## Decisión registrada para la copia del 22-nov

`coppadresd-data-20261122` no entra en la regla operativa de 30 días para
snapshots ordinarios. El [runbook del corte][runbook] y [KAN-183][kan183]
lo identifican como insumo de la recuperación de coppadresd. [KAN-184][kan184]
lo necesita para restaurar y probar el backend legado de forma separada.
AWS lo mostró disponible y cifrado el 27-nov. Sigue protegido para el
preflight incluso si vence el plazo nominal o alguien marca su fila como
`eligible`. Lo anterior no establece retención indefinida. Al terminar el
uso de recuperación, el custodio revisa el expediente y deja una disposición
nueva antes de quitarlo del inventario protegido.

Los otros tres snapshots observados no son elegibles solo por cumplir edad.
Para el del 18-nov, la evaluación de los 30 días empieza el 18-dic.
Para los dos del 25-nov, el 25-dic. Los cálculos usan la hora real del
snapshot. Registrar necesidad de rollback, copias compartidas, custodia
clínica y suspensiones antes de aprobar disposición de cada uno.

[runbook]: https://github.com/LFDIAZDEV2209/coppadresd-infra/blob/25d99580dd71143304f28f1239db1a615e229d88/PROMPT-LEVANTAR-COPPADRESD.md
[kan183]: https://diomedescerda.atlassian.net/browse/KAN-183?focusedCommentId=10493
[kan184]: https://diomedescerda.atlassian.net/browse/KAN-184

## Cómo ejecutar

Un custodio captura metadatos sin descargar contenido ni exportar eventos de
logs. Usa la cuenta esperada de forma independiente del archivo. El wrapper
AWS CLI debe paginar la lista completa, sin `--no-paginate` ni `--max-items`.
En el manifiesto local, combinar:

| Campo | Evidencia |
| --- | --- |
| `account`, `observedAt` | `sts get-caller-identity` y hora UTC de la captura. |
| `buckets` | Por bucket, la salida JSON completa de `s3api list-object-versions`, incluidos `Versions` y `DeleteMarkers`. Incluir el bucket en la captura aunque esté vacío. |
| `snapshots` | Filas pertinentes de `rds describe-db-snapshots`. Incluir las copias encontradas en el alcance y verificar cuentas/regiones y snapshots compartidos por separado. |
| `protectedSnapshots` | Mapa ID → referencia. Para el corte legado incluir `{"coppadresd-data-20261122": "jira:KAN-183/KAN-184"}`. |
| `activeGroups` | Nombres exactos de grupos en las definiciones de tarea de servicios activos, no los grupos que solo coincidan con un prefijo. |
| `logGroups`, `logPolicyOverrides` | Filas de `logs describe-log-groups` y mapa de los grupos con política efectiva a nivel de cuenta. Solo usar `{}` si se comprobó que no hay overrides aplicables. Usar `null` si no se pudo comprobar. |
| `reviews` | Primera captura `{}`. Después, recurso → `{"decision": "eligible", "reference": "expediente:referencia"}` o `"decision": "retain"`. Cotejar alcance con el custodio, suspensiones, copias de recuperación y verificación de restauración. |

```bash
umask 077
python3 scripts/qa/kan-170/cloud_retention_review.py \
  --account 933629770820 --evidence "$MANIFIESTO_PRIVADO"
```

Las pruebas locales no necesitan AWS.
`python3 -m unittest discover -s scripts/qa/kan-170 -p 'test*.py'`

Las claves de objetos y los IDs de versión quedan en el manifiesto privado,
fuera de Git, Jira y chats. La salida solo usa IDs derivados. El preflight
rechaza inventarios paginados sin terminar, no propone versiones actuales ni
marcadores y no presume el orden si faltan elementos o hay empates de fecha.
Para una versión no actual, la edad empieza con su sucesor inmediato, que
puede ser un marcador. El vencimiento de S3 redondea a la siguiente medianoche
UTC. Si hubo restauraciones de una versión o cambios en el historial,
confirmar el punto de partida con el custodio. No se
debe instalar una regla general en el bucket activo porque también se aplica
a objetos existentes y allí hay datos con distintas reglas. [Cálculo y
filtros de S3][s3-age] · [Alcance retroactivo][s3-existing]

Para CloudWatch, la configuración de 30 días solo aplica a logs ordinarios.
En un grupo mixto, clasificar y preservar primero la evidencia necesaria o
separar los destinos futuros. Verificar la política efectiva de cuenta.
Reducir la retención de grupo afecta logs históricos, y el vencimiento no
prueba purga inmediata. No cambiar todos los grupos por prefijo. [Retención de
CloudWatch Logs][logs]

El piloto usa el grupo nuevo `/ecs/cooppadresd-auth-ordinary` con 30 días en
Ohio y deja intacto `/ecs/cooppadresd-auth`. Lo enruta únicamente el
despliegue de Auth. Verificar grupo, retención, tareas sanas y que lleguen
streams después del rollout, sin leer su contenido. Para volver al grupo
histórico, fijar la variable `AUTH_ORDINARY_LOGS_ENABLED_DEV=false` antes
de un nuevo despliegue. Esto no borra ni modifica la retención de ningún
grupo. Mantener el resguardo de incidentes por el canal correspondiente.

[s3-age]: https://docs.aws.amazon.com/AmazonS3/latest/userguide/intro-lifecycle-rules.html
[s3-existing]: https://docs.aws.amazon.com/AmazonS3/latest/userguide/lifecycle-expire-general-considerations.html
[logs]: https://docs.aws.amazon.com/AmazonCloudWatch/latest/logs/Working-with-log-groups-and-streams.html

## Qué cuenta como cerrado

Registrar la última lectura de las versiones afectadas, el estado de las
copias y su destino protegido, y el resultado de restauración y reconciliación
en el [expediente por fuente](coverage.md). No marcar `expired` porque la fecha
calculada pasó. Obtener acuses independientes de proveedores cuando les
corresponda. El corte del producto legado no reemplaza la conciliación y
manifiestos por módulo de KAN-118/KAN-122.
