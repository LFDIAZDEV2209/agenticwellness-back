# Evidencia de preparación BACK 4.11

15-11-2026, codex-back, producto **e0a9c29**. Soporte preparado; recorrido visual
4.11 pospuesto por rediseño, **sin declarar la fase cerrada**.

Contrato y guion completo:
[QA-4.11.md](../../QA-4.11.md).
Metadata sin secretos:
[fixture-metadata.json](fixture-metadata.json).
Helper:
[prepare-back-4.11.py](../../../../../scripts/qa/program-assignment/prepare-back-4.11.py).

- Paciente nuevo **8ce7fbef**: nueve tests enviados por API, una propuesta y una
  inscripción **PendingAssignment**. Nutrición publicó un plan propio de 30 días;
  rutina Caminata Activa y secuencia de siete celdas quedan en la propuesta.
  Cinco referencias de podcast conservan audio demo: **bloqueo 1.14 vigente**,
  sin afirmar reproducción completa ni activación inmediata del programa.
- Paciente IA separado **5e3a337e**, inscripción **aa946028**, semana 2 del
  22–28/11: **cinco sugerencias draft**, sin apply y con semana/auditoría intactas.
  Proveedor del borrador **Postman sintético**, handler/guard/store/EF reales.
  Dos intentos live previos devolvieron cero sugerencias y se conservaron.
- Caída de proveedor: **3/3 PostgreSQL aisladas**, 0 skip/fail, y **2/2 clientes
  .NET contra Postman**, tráfico 200/200/503 por cliente. Preservación de draft,
  propuesta y snapshot; sin aplicar ante 503/timeout/JSON inválido/cancelación.
- Lecturas reales separadas de API5122: **9 requests / 19 aserciones**, plan APP
  de 30 días, estados pendientes/activos correctos y GET del borrador sintético.

## Artefactos

| Archivo | Evidencia |
| --- | --- |
| fixture-check.json | Estado final de los dos pacientes, hashes y sentinelas protegidos |
| readonly-endpoints.json | Estados HTTP/estructura/hash de nueve lecturas, sin cuerpos clínicos ni URLs firmadas |
| podcast-catalog-check.json | Referencias de la propuesta y cinco claves demo, sin verificación física S3 |
| canonical-reader-input.json / canonical-options.json | Captura de guard/store/clinical/safety reales; 14 slots, 22 opciones rutina/69 podcast |
| synthetic-suggestion-seed.json / .log | Inserción del brief propio vía handler real y Postman HTTP; snapshot inalterado |
| suggestion-seed/ | Runner QA C# restringido al UUID propio; nunca llama a apply |
| degradation-gates.json / *.trx / *.log | Dos clientes de simulación y tres casos PostgreSQL aprobados al SHA e0a9c29 |
| *-simulation.ndjson | Requests reales hacia el stand-in, sin cuerpos ni secretos |
| owned-processes.json / owned-process-tree.json / processes-closed.json | Raíces/descendencia propias y cierre por identidad verificada |
| sentinel-diff.json / sentinel-reconciliation.json | Alta concurrente de un plan ajeno; hash exacto de las siete filas originales preservado |
| readonly-endpoints-before-scope-reconciliation.json | Preflight que detectó el cambio global; no se descarta esa evidencia |
| run-degradation.ps1 | Reproducción local aislada de 2 clientes y 3 pruebas PG; cierra solo stand-ins propios |
| evidence-manifest.json | SHA-256 de fuentes, metadata y resultados |

`canonical-options.json` procede del lector ya compilado para 4.8 sobre 4c7a811;
es diagnóstico de opciones con fuente sin cambios en e0a9c29, no se cambia su SHA
de origen. El runner que guarda el brief referencia el checkout e0a9c29 exacto.
La metadata guarda generación sintética explícita; no se certifica salida live
de un LLM con alternativas ni QA de pantallas. El programa pending no se hace
pasar por Active. No se cambió Program:Assignment:Enabled ni se movieron fechas.

Los helpers rechazan tocar UUIDs protegidos y resetear estados consumidos.
No usar prepare/complete-tests sobre la fixture APP anterior. `check` es lectura;
`seed-simulated-suggestion` necesita el mock propio en 4533 solo si aún no existe
el brief sintético. Las simulaciones usadas ya están cerradas.

Sin cambios a `0c487f75`, comida/cierre rev7/33 R7, los siete planes y cuatro
cierres ajenos originales ni fixture APP aparcada. Se detectó un octavo plan
ajeno publicado durante el trabajo; los hashes prueban que las siete filas
originales permanecen idénticas. No se afirma que el conjunto global no creció.
Las escrituras de este helper fueron setup de los dos pacientes nuevos,
sus evaluaciones/asignaciones/nutrición, relación con el generador y briefs QA;
Auth crea sesiones de login. Sin migraciones, push ni tasks.md. No se reinició
el stack; procesos de simulación propios verificados y cerrados. Producto sigue
e0a9c29; la documentación nueva del módulo queda en el worktree para revisión.

## Archivo versionado

Copia revisada de la evidencia compartida original. Los hashes originales se
conservan en origin-evidence-manifest.json; evidence-manifest.json identifica
esta copia y los ajustes de rutas para el repositorio. fixture-metadata.json
es el corte histórico sin secretos, no un reemplazo de la metadata operativa.
Las fuentes de los helpers resuelven el workspace local documentado; sus
comandos de escritura requieren las autorizaciones y guards originales.

Aclaración de alcance: **1.14 es la reparación reversible de huecos con §5.1,
solo sobre items program; no es la carga de mp3**. Las referencias demo son
un hallazgo del catálogo. La simulación operativa y sus limitaciones se
documentan aparte; este archivo conserva lo observado durante QA4.11.
