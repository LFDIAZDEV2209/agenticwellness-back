# Evidencia 3.21 — 16-11-2026

Fuente de producto: `9a59f3392021b46ec1e6f5f68c74886b58f18117`.
Consulta canónica local: **0 días con hueco**, clases **0/0/0**, 175 inscripciones
activas/59.778 días. Continuidad: **590 días / 3 inscripciones**, menú final 30,
cero inválidos. Fixture protegida incluida explícitamente: una inscripción,
cero huecos. Transacción READ ONLY / REPEATABLE READ terminada con ROLLBACK.

Gates actuales: build **0 errores/15 advertencias**, herramienta **0/0**;
unitarios **2997 aprobados/105 omitidos/0 fallos**; integración PostgreSQL principal
**222 aprobados/2 omitidos/0 fallos** (13 min 54 s). Omisiones: matriz especial
`ReleaseMerge_*` sin habilitación de inventario Git y proveedor HTTP sin
`COP_PROGRAM_NARRATIVE_SIM_URL`. La matriz de migraciones del preflight anterior
se conserva como evidencia histórica, no como una ejecución nueva.

| Artefacto | Qué acredita |
| --- | --- |
| database-audit.json / database-audit.log | Handler, guard, store y resolver reales; totales, desglose, continuidad, historial y fixture incluida |
| local-template-state.json | Plantillas locales: activa 83 específicas/7 genéricas; legacy 7 genéricas; cero demo. Hash de las filas completas observadas y genéricas resumidas |
| deployed-observation.json | Resultado real RDS READ-ONLY vía ECS `cooppadresd-dbops:4`, aportado por Luis; transcripción estructurada, sin atribuir ejecución ni hash del original a este carril |
| sql-review.json | Revisión estática de seis UPDATE por archivo, solo media_id; **sin ejecutar** apply/reverse |
| build.log / reader-build.log | Compilación de solución y herramienta con salida separada |
| unit-results.json / unit.log | Contadores del TRX y 45 resultados de resolver/calendario/adherencia, sin PostgreSQL |
| postgres-results.json / postgres.log | Suite principal PostgreSQL aislada y resultados individuales, incluidos continuidad y sucesora |
| gates.json | Estados exactos de los gates de esta sesión, separados del preflight histórico |
| operations.json / cleanup.json | Incidente de Docker, autorización, procesos y listeners preservados |
| raw-artifacts.json | Ubicación, tamaño y SHA-256 de TRX crudos conservados fuera de Git |
| MANIFEST.json | SHA-256 de toda la evidencia versionada y las fuentes/comandos relevantes; se excluye a sí mismo |

Comandos reproducibles en [VERIFICACION-3.21](../../VERIFICACION-3.21.md).
La herramienta está en `scripts/ProgramPhaseVerification`; el wrapper
`scripts/qa/program-assignment/verify-3.21.ps1` mantiene la conexión exclusivamente
en memoria. Las pruebas reciben conexión local y crean/eliminan sus bases aisladas.
No se aplicaron migraciones ni reparaciones sobre la BD local compartida.

Los TRX grandes se conservan en
`.ai/operations/organizar-experiencia-bienestar/3.21-20261116/`; los resúmenes
versionados se extraen de sus contadores/resultados XML. Los logs versionados son
UTF-8 sin BOM. Los hashes del manifiesto usan bytes de blobs Git (LF normalizado
por sus atributos), para reproducirse también desde otro checkout Windows.
Los hashes de TRX crudos usan sus bytes originales, sin normalización.

Los intentos iniciales sin PostgreSQL no son gates válidos: se registra el fallo
de conexión y la interrupción, y se distinguen de la repetición tras la autorización
de Luis para iniciar Docker. La primera invocación tuvo además una ruta DLL
incorrecta, corregida antes de la auditoría válida. Se conserva esa incidencia
en metadata, sin reutilizar resultados fallidos como evidencia de aprobación.

RDS: activa v4/12 semanas, siete genéricas válidas/ninguna específica; legacy
Archived v3/83 semanas, 116 activos, seis demo weekdays 1–6. Catálogo 68 Published/
34 con audio real; demos 1000000001 y 55551234 están en la activa. El acceso propio
por bastión falló; esta fuente fresca de dbops sustituye el handoff. No incluye
un resultado de 1.10b desplegado. [Residual y SQL](../../RESIDUAL-DESPLEGADO-3.21.md).

Sin push, `tasks.md`, sincronización/archivo de specs ni escrituras en RDS.
El cierre administrativo sigue pendiente del owner. Los 15 fallos PostgreSQL
legados del release-preflight no se consideran resueltos por este paquete.
