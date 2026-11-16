# Memo de decisión — bloqueo de 1.14

**Estado: pendiente de decisión de producto.** 15-11-2026. Ninguna opción autorizada
ni implementada por este memo.

La [simulación local](REPAIR-1.14.md) encontró **50.960 días con huecos**, todos de
podcast. Insertar `program` en memoria para 50.838 días futuros elegibles reparó
**0** con la regla actual: futuras Locked releen plantilla/semanal y genérica.
La fixture protegida `0c487f75` conserva **54 días con hueco**. 1.14 es reparación
reversible de datos según §5.1, no carga de mp3.

| Opción | Impacto | Riesgos y condiciones |
| --- | --- | --- |
| **A. Prioridad de snapshots program en futuras Locked** | Cambia el producto: una copia materializada puede ganar a la plantilla vigente. Haría efectiva una reparación en esos snapshots, con selección de contenido aprobada. | Puede congelar contenido obsoleto e impedir que publicar una plantilla actualice futuras. Exige definir prioridad respecto de individual, actualización/invalidez de copias, reversa y compatibilidad. El cambio de lectura también puede afectar fixtures sin escribir sus filas. No resuelve por sí solo los días excluidos. |
| **B. Cero acotado, excluyendo la fixture** | Cambia el criterio de aceptación mediante autorización explícita; conserva la fixture. Deben mostrarse juntos el total global y el del alcance aceptado. | Excluir 54 deja **50.906**, no cero: aún hace falta una reparación efectiva. Riesgo de presentar el cero parcial como global. Requiere exclusión identificada, motivo, responsable y fecha de revisión; conservar 1.10b global sin ocultar filas. |
| **C. Mantener 1.14 como deuda y cerrar 3.21 con excepción** | Mantiene reglas, datos y fixtures. Permite proponer el cierre de 3.21 con una excepción explícita al requisito de cero. | Acepta que siga faltando contenido. El owner debe aprobar la excepción, su impacto y el seguimiento con responsable y plazo. No acredita cero, reparación/reversa ni cierre de 4.11 o de toda la fase. |
| **D. Reparar en plantilla/plan** | Cambio editorial global: corrige las fuentes que ya consume §5.1. Para los huecos actuales de podcast, el punto relevante es la plantilla/secuencia, no el plan nutricional. | Afecta a múltiples pacientes y puede alterar el contenido efectivo de la fixture protegida. Requiere aprobación editorial del contenido, alcance e impacto, simulación, respaldo y reversa. Conservar individuales y datos cerrados. No equivale a autorizar cualquier sustituto publicado. |

**Recomendación:** elegir **C para el cierre administrativo inmediato**, solo con
aceptación expresa del owner, manteniendo visibles los conteos y 1.14 abierta.
Es la alternativa que conserva las restricciones actuales sin introducir una
política de asignación o de lectura implícita.

Para resolver la deuda, evaluar **D** si producto quiere mantener que futuras
reflejen la plantilla vigente. Reservar **A** para una decisión deliberada de
congelar copias futuras. **B es complementaria**, no una reparación: puede
acompañar la solución elegida si se autoriza un objetivo acotado. En cualquier
cierre, definir expresamente cómo se tratan los 54 días protegidos; mientras
permanezcan como huecos dentro del alcance, el total global no puede ser cero.

Este memo no cierra 3.21, no modifica criterios, código o datos y no ejecuta
ninguna reparación. La decisión debe nombrar opción o combinación, alcance y
owner antes de reanudar 1.14.
