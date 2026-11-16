# 1.14 — opción D autorizada, plantilla reparada y arranque A2/F2

Luis autorizó el 15-11-2026 reparar la plantilla activa porque el proyecto aún
no está en operación. Se leyeron PLAN-1.14-AUDIOS, ANSWERS-OWNER A2/F2/C1 y
ARQUITECTURA §5.1. No se alteró la precedencia de futuras Locked.

## Reparación local y reversa

Plantilla `program-coppaddresd-83-days`, UUID
`75cef1a7-dd75-40cb-8d34-470b8eed4ddf`, Active, versión 3, 12 semanas/83 días.
La operación solo escribe sus filas podcast: conserva los siete IDs genéricos
con referencias válidas y agrega 83 celdas específicas (90 filas físicas).
Los respaldos genéricos cubren el calendario ISO cuando el inicio no es lunes,
incluido el domingo de la semana 12; no se agrega un día 84.

Se simuló, guardó respaldo JSON antes de escribir, aplicó, verificó, revirtió
exactamente a las siete filas originales y reaplicó/verificó el estado final.
Cada mutación usa transacción, bloqueo de cabecera y comparación del contenido
esperado antes de sobrescribir. La reversa también compara el estado aplicado.
El primer apply se detuvo sin escrituras al variar el hash del conjunto de semanas
del stack vivo: los sentinelas protegidos seguían iguales. Se conservó esa
simulación y se acotó el sentinela a cierres, antes de simular y respaldar de nuevo.

Los 13 medios Published corresponden a Ep. 1–7 y 14–19. Se comprobó HEAD,
tamaño, tipo y firma, y luego descarga/decodificación completa de los 13 MP3
con FFmpeg: **13 válidos, cero errores**. Solo lectura en S3, sin subir ni
publicar archivos. No se cambió ningún medio, plan, estado o versión de plantilla,
snapshot individual, semana, evaluación, cierre o prescripción publicada.
Los hashes protegidos de `0c487f75`, R7 y cierres de 3.20 permanecieron iguales.
Su contenido efectivo futuro sí puede mejorar por leer la fuente compartida.

**Desviación temporal C1:** la base solicitada de 42 publicados se reduce a 13
audios reales. El catálogo actual tiene 37 episodios, 24 aún con MP3 pendiente;
completarlos deja 37, no acredita por sí solo la base de 42. No se borran audios
de prueba del catálogo (F2), pero ninguna de estas 90 referencias usa claves demo.
La cuadrícula repite el ciclo de 13; cada semana editorial tiene podcasts
distintos. Por el calendario ISO y el respaldo genérico, la menor distancia
observada para inicios no lunes es 5 días; no se promete 13 días entre escuchas
para todo inicio. Los siete inicios posibles cubren 83 días sin huecos.
C1 no establece una separación mínima de 42 días: esa frase del campo descriptivo
`c1Exception` del primer respaldo es imprecisa y se conserva solo por integridad
del archivo; el generador y este contrato ya usan la formulación correcta.

## Consulta real 1.10b, sin exclusiones

Captura final: 16-11-2026 00:53:01 UTC (15-11, 19:53 America/Bogota), con el
handler, store y resolver reales, transacción READ ONLY/REPEATABLE READ.

| Alcance | Inscripciones | Días evaluados | Días con hueco |
| --- | ---: | ---: | ---: |
| Plantilla activa reparada | 186 | 1.128 | **0** |
| Plantilla archivada `default-83w` | 116 | 58.825 | **50.322** |
| Global | 302 | 59.953 | **50.322** |

Todos los huecos residuales son podcast; ejercicio/nutrición: 0/0.
`0c487f75` permanece en la consulta y presenta **0** huecos efectivos, sin cambiar
sus filas protegidas. Durante la reversa, el total fue 51.019: la reparación
quita **697 días con hueco** en ese corte. El diagnóstico anterior de 50.960
tenía otro corte y 300 inscripciones; no se usa como denominador equivalente.
No se reparó ni migró la plantilla archivada, fuera del alcance autorizado.
**No hay cero global ni aceptación implícita de una excepción de 3.21.**

## Garantía A2/F2 para nuevas baterías iniciales

El submit real del último requerido crea una inscripción **Active**, semana 1,
racha y doce semanas, la primera materializada con nutrición/rutina/podcasts, dentro de la misma
transacción clínica. Se valida todo el calendario efectivo de 83 días antes de
confirmar, sin propuestas ni prescripciones que oculten defaults incompletos.
Si falta plantilla/contenido válido, se rechaza el submit y se revierte el último
test: nunca se informa batería completada con un programa nuevo vacío.

El bloqueo por paciente precede al de asignación: los dos últimos tests
concurrentes se observan y crean una única inscripción. Replays no duplican.
No se reinician programas existentes/completados (A4), ni se purgan sus puntajes.
Las futuras Locked conservan snapshot vacío y se materializan al activar, para
recibir la plantilla vigente según A3/§5.1 sin congelar rutinas por adelantado;
no se altera la prioridad individual o la conservación de cierres.

El gate Program.Assignment.Enabled solo condiciona el enriquecimiento opcional
del evento legacy. Una propuesta/narrativa fallida o IA no disponible conserva
los defaults. La nutrición automática personalizada sigue su workflow separado;
el plan general completo ya está disponible mientras ese trabajo termina.
El submit no publica una prescripción ni aplica una sugerencia IA.
Se mantiene el pipeline CQRS clínico existente después de validar defaults;
no se agrega KPI, agregado nuevo ni contador por inscripción.

La corrección está en fuente y probada en PostgreSQL aislado. **API 5122 continúa
ejecutando e0a9c29**: no se reinició ni se afirma que ya ejecute este nuevo arranque.
La fixture histórica de 4.11 permanece PendingAssignment y no se migró.

## Validación del parche

Build final aprobado, 0 errores; unitarios **2801 aprobados/105 omitidos**.
PostgreSQL aislado: **159 escenarios verificados** del filtro de 4.12 ampliado
con A2/F2. La corrida completa fue 158 aprobados/1 fallo de fecha en la fixture
del resumen; después de ajustar solo ese test, el reintento fue **1/1 aprobado**.
Se conservan ambos TRX; no se presenta como una única corrida 159/159 exitosa.
La corrida ampliada anterior (212 casos) y los cambios para hacer deterministas
las fechas de las fixtures también quedan documentados en el paquete.

## Evidencia y reproducción

[Paquete y manifiesto](evidence/1.14-D/README.md), réplica del directorio compartido
`revision-1.14-d`. Respaldo sin secretos; conexión solo en memoria desde el helper
local. Scripts en `scripts/qa/program-assignment/repair-template-1.14.py` y
`verify-audio-1.14.py`; auditoría en `scripts/ProgramContentAudit`.

Secuencia del script: `simulate --backup <nuevo.json> --report <sim.json>`,
`apply`, `verify`, `reverse`, `apply`, `verify`, siempre con el mismo respaldo y
reportes distintos. Una nueva simulación exige otro nombre de respaldo; ningún
archivo existente se reemplaza. Verificación de audio recibe respaldo y salida.
No reutilizar el apply ciegamente si el catálogo o los sentinelas cambiaron.
