# Evidencia de simulación 1.14

15-11-2026, codex-back. [Diagnóstico y alcance](../../REPAIR-1.14.md).

**50.960 días con hueco antes y después**, todos podcast; ejercicio/nutrición 0.
**0 días reparables por la materialización permitida de program futuro** bajo
la resolución actual. Los 54 huecos de la fixture excluida impiden además el
cero global. 50.838 inserciones contrafactuales solo en memoria no cambiaron el
resultado del resolvedor. La simulación usa la BD local real en READ ONLY y
REPEATABLE READ; ambos valores se comprueban en PostgreSQL.

No se ejecutó una escritura sin efecto sobre el objetivo ni una reversa vacía
para darla por probada. Apply y reversa permanecen **sin ejecutar**, pendientes
de definir una fuente efectiva y el alcance de cero compatible con la fixture
protegida. No se modificó el resolvedor, el catálogo, la plantilla ni individuales.

| Archivo | Resultado |
| --- | --- |
| simulation.json / simulation.log | Conteos canónicos y diagnóstico contrafactual; cero cambios de BD |
| audit-after.json / audit-after.log | Consulta 1.10b repetida, 50.960 días con hueco; no está en cero |
| protected-before.json / protected-after.json | Fixture 0c487f75, R7, publicaciones/cierres originales y fixture APP intactos |
| gates.json / build.log | Build de solución: 0 errores, 15 advertencias existentes |
| resolver-regression.trx / .log | **36/36** casos de resolución y precedencia aprobados, 0 omitidos |
| results-summary.json | Estado bloqueado, operaciones realmente ejecutadas y límites |
| evidence-manifest.json | SHA-256 de esta evidencia y de las fuentes |

Código de producto base e0a9c29. QA4.11 y su evidencia se guardaron en **9326c9b**
por instrucción del owner; el código de resolución no cambió. Se añadió únicamente
una opción diagnóstica a scripts/ProgramContentAudit, sin apply. Los 36 tests se
ejecutaron sobre binarios e0a9c29, coincidentes con ese código de resolución;
el CLI diagnóstico se compiló y ejecutó con sus fuentes nuevas.

Sin push, tasks.md, migraciones ni reinicios. Los runners propios terminaron;
los procesos compartidos siguen intactos. La falta de apply/reversa y de cero
global se declara expresamente, sin atribuir 1.14 a la llegada de archivos mp3.
