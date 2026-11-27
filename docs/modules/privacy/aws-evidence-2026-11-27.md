# KAN-170 · Evidencia AWS del 27-nov-2026

Consulta de metadatos a las 21:00 UTC, cuenta 933629770820, región Ohio. Se
verificó la identidad de AWS antes de la captura. No se descargaron objetos,
no se leyeron eventos de aplicación, no se restauraron copias y no se
cambiaron datos ni configuraciones. Se ejecutó el [preflight](cloud-retention.md)
sobre el inventario completo de ambos buckets de almacenamiento y los grupos
usados por las tareas ECS activas.

## Versiones no actuales de S3

En `cooppadresd-storage-prod` se observaron 166 versiones actuales, cinco no
actuales y nueve marcadores. No había configuración de lifecycle. No se
observaron objetos, versiones ni marcadores en el bucket secundario. El bucket
de backups no devolvió objetos. Cada una de las cinco versiones no actuales
respondió a `HeadObject` y no tenía etiquetas de versión, Object Lock ni
metadatos personalizados. Esta ausencia no descarta una suspensión operativa.

| Ref. derivada | Tipo observado | Llegó a no actual (UTC) | Umbral S3 (UTC) | Resultado |
| --- | --- | --- | --- | --- |
| `97abcadd284c` | Texto, origen no comprobado | 14-ago 19:34 | 14-sep | Pasó el umbral. Validar custodio y si sirve para recuperación o evidencia. |
| `f0ff940a68ba` | Script de shell, posible importación | 01-oct 21:21 | 01-nov | Pasó el umbral. Validar uso por DB Ops y dependencias de restauración. |
| `0148980e0377` | Audio en la ruta de podcasts | 08-nov 16:19 | 09-dic | Aún no alcanza el umbral. Cotejar con el retiro del módulo. |
| `d0353abb0f81` | Imagen en la ruta de Food AI | 16-nov 14:05 | 17-dic | Aún no alcanza el umbral. Conciliar con Food AI y su expediente aplicable. |
| `2f464bc5ccc1` | Imagen en la ruta de media | 17-nov 14:27 | 18-dic | Aún no alcanza el umbral. Confirmar vínculo antes de caducar. |

Resultado del preflight. Tres no han llegado al umbral, dos requieren revisión
y ninguna se clasificó como `eligible`. El tipo MIME y la ruta son indicios,
no evidencia suficiente para aplicar una política de disposición.

## CloudWatch

Las seis definiciones ECS activas apuntaban a los grupos Auth, AI, API,
Food AI, Community y frontend bajo `/ecs/cooppadresd-`. Ninguno tenía
`retentionInDays`. La consulta regional de políticas de cuenta devolvió cero
políticas de retención y cero suscripciones de cuenta. Los seis grupos
tampoco mostraron suscripciones propias. Esto verifica la configuración
efectiva en el alcance, no descarta exportaciones históricas por otras vías.

Los metadatos de streams reportaron primeros eventos entre el 24-ago y el
10-sep según el grupo. Reducir el límite de esos grupos afectaría contenido
histórico. Auth contiene rutas que registraban identificadores directos.
Antes de aplicar 30 días al histórico, identificar y conservar aparte las
excepciones de incidente o auditoría. Para flujos nuevos, usar destinos
ordinarios con retención de 30 días y separar evidencia con conservación
distinta. No usar la antigüedad para asumir que todo en los grupos activos
queda autorizado para eliminación.

## RDS y otras copias

- PITR activo con 7 días, de 20-nov 20:55 UTC a 27-nov 20:55 UTC.
- Los tres snapshots manuales del primario no alcanzaban el umbral de 30
  días. CloudTrail reportó tres operaciones `DeleteDBSnapshot` del primario
  el 25-nov a las 22:26 UTC sin error, lo que explica la diferencia con el
  inventario anterior. No implica que nunca existieran copias de esos snapshots.
- `coppadresd-data-20261122` está disponible, cifrado, mide 200 GiB y
  reporta PostgreSQL 18.3 y una clave administrada por AWS habilitada. AWS
  registra la creación física el 26-nov a las 00:29 UTC. El nombre y el
  snapshot no prueban por sí solos que todos los datos correspondan al
  corte lógico del 22-nov. Mantener la excepción de [KAN-183/KAN-184](cloud-retention.md)
  y comprobar esa fecha y los conteos en la restauración aislada.
- Ninguno de los cuatro snapshots consultados mostró permisos públicos ni
  para otras cuentas. No se observaron snapshots compartidos coincidentes
  por origen en Ohio, Virginia u Oregón ni exportaciones de estos orígenes
  en Ohio. No es evidencia de ausencia de copias en todas las cuentas
  y regiones ni de copias fuera de AWS.

## Trabajo que queda

El responsable de almacenamiento puede cotejar las dos referencias
vencidas contra las claves del manifiesto privado para decidir retención o
retiro. Operaciones debe separar las excepciones del histórico de CloudWatch
antes de reducir el límite y verificar que la nueva política corresponda a
los destinos realmente usados. Además hacen falta el informe de
restauración aislada y las confirmaciones de custodios y proveedores del
[expediente por fuente](coverage.md). Nada en esta consulta autoriza borrar
un expediente o retirar el snapshot de recuperación.
