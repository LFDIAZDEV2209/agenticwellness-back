# 3.21 — verificación local y continuidad nutricional

Corte **16-11-2026 13:54:12 UTC** (08:54:12 America/New_York), fuente
`9a59f3392021b46ec1e6f5f68c74886b58f18117`, rama `org/f4-coppAddresdBack`.
**1.10b global = 0**, sin exclusiones. La aprobación administrativa sigue pendiente
del owner; no se archivaron/sincronizaron specs ni se modificó `tasks.md`.

## Resultado real

Se ejecutaron el handler `GetContentGapsQueryHandler`, store, guard y resolver
actuales, sin host/jobs ni IA. El actor administrativo efímero solo autoriza
`Program.View`; esto no prueba autenticación HTTP/JWT. La herramienta congela
el mismo instante para handler/desglose y exige que ambos totales coincidan.
Conexión y transacción verifican `transaction_read_only=on`, REPEATABLE READ,
con timeout; termina con ROLLBACK. **Cero escrituras.**

| Alcance | Inscripciones activas | Días evaluados | Días con hueco |
| --- | ---: | ---: | ---: |
| `default-83w` archivada, 83 semanas | 116 | 58.709 | **0** |
| `program-coppaddresd-83-days` activa | 59 | 1.069 | **0** |
| Global | **175** | **59.778** | **0** |

Podcast / ejercicio / nutrición: **0 / 0 / 0**. Pacientes/inscripciones con hueco:
**0 / 0**. `0c487f75` permanece incluida y con **0 huecos efectivos**.
171 inscripciones tienen fechas restantes; cuatro activas están agotadas y no
generan días según el contrato existente. Rango: 16-11-2026–16-04-2028.
El programa nuevo conserva 83 días; el legado, 581.

Este es el denominador actual, distinto del corte histórico de 302 inscripciones
y 59.953 días. No se equiparan capturas de fechas/estados distintos. La herramienta
no cambia estados, fechas, inscripciones ni contenido.

## Continuidad nutricional

| Fuente efectiva | Días |
| --- | ---: |
| Motor/respaldo (`engine`) | 59.113 |
| Prescripción vigente (`prescription`) | 75 |
| Continuidad (`continuity`) | **590** |

Los 590 días pertenecen a **3 inscripciones** y usan el **menú 30**, último menú
de su prescripción propia. Cada día se comprueba contra propiedad, referencia de
prescripción, fecha posterior al fin del ciclo, número igual a la duración del
ciclo y presencia de ese menú. **0 días inválidos**.
Los tests existentes complementan el corte con sucesora tardía, publicación local,
versiones históricas, fallback si falta el menú y coincidencia en misión, Mi plan,
calendario y adherencia. Resultados exactos en el paquete.

## Gates y reproducción

[Evidencia y gates](evidence/3.21/README.md),
[`database-audit.json`](evidence/3.21/database-audit.json) y
[`MANIFEST.json`](evidence/3.21/MANIFEST.json): comandos, salidas y SHA-256.
Los gates históricos de release-preflight y sus 15 fallos PostgreSQL legados
permanecen separados; esta tarea no los declara corregidos.

| Gate actual | Resultado |
| --- | --- |
| Build solución | **PASS**, 0 errores / 15 advertencias existentes |
| Build herramienta de lectura | **PASS**, 0 errores / 0 advertencias |
| Unitarios principales | **PASS**, 2.997 aprobados / 105 omitidos / 0 fallos |
| Integración principal PostgreSQL aislada | **PASS**, 222 aprobados / 2 omitidos / 0 fallos, 13 min 54 s |

Las dos omisiones PostgreSQL son `ReleaseMerge_MigracionesNuevasYSaltosDesdeAmbasRamas_NoColisionanNiPierdenModelo`
(requiere habilitar el preflight con inventario Git de ambas ramas) y
`ClienteReal_ContratoBilingueY503DeProveedor` (sin `COP_PROGRAM_NARRATIVE_SIM_URL`).
No se presentan como pruebas ejecutadas. Los tres escenarios PostgreSQL de
continuidad/menú final, versiones históricas y sucesora tardía sí pasaron; sus
nombres y outcomes completos están en `postgres-results.json`.

```powershell
dotnet build -p:OutputPath=bin/Verification321/ -v quiet
dotnet build scripts/ProgramPhaseVerification -p:OutputPath=bin/Verification321/ -v quiet
dotnet test tests/CoppAddresd.UnitTests --no-build -p:OutputPath=bin/Verification321/ --results-directory docs/modules/program-progress/evidence/3.21 --logger 'trx;LogFileName=unit.trx' -v quiet
powershell -NoProfile -File scripts/qa/program-assignment/verify-3.21.ps1 -Mode Audit
powershell -NoProfile -File scripts/qa/program-assignment/verify-3.21.ps1 -Mode Postgres
```

El wrapper lee configuración local existente y entrega la conexión solo por entorno
de proceso, restaurándolo al terminar; nunca copiar configuración a evidencia.
`-ConfigPath` permite otro archivo seguro y `-Evidence` otro directorio existente.
La compilación usa salida separada. La suite crea/elimina sus BD aisladas;
no aplica migraciones sobre la compartida. Historial local leído: **113 migraciones**,
cero pendientes/desconocidas; las seis de ciclos/intakes/revisiones F3 están aplicadas.

## Entorno y límites

Inicialmente PostgreSQL/Valkey no escuchaban y Docker no estaba disponible, aunque
API/Auth/Gateway/front seguían arriba. La auditoría falló al conectar y la primera
suite PostgreSQL se interrumpió sin resultado aceptable. Luis autorizó iniciar
solo Docker y los contenedores existentes PostgreSQL/Valkey. Sus políticas de
arranque levantaron automáticamente `coppAddresd`, `coppAddresd-valkey` y
`exo-track-db`; no se emitieron start/stop para este último ni otros contenedores.
PostgreSQL/Valkey quedaron healthy. API/Auth/Gateway/front conservaron sus PID.

La captura local de plantillas muestra **83 específicas + 7 genéricas** en la activa
y **0 demo** en las siete genéricas de la archivada. Consulta y hashes en el paquete.
No hubo reparación ni migración de datos compartidos durante esta tarea.

**RDS tiene evidencia distinta:** consulta READ-ONLY real vía ECS
`cooppadresd-dbops:4` del 16-11, aportada por Luis: activa v4 con siete genéricas
válidas/ninguna específica; archivada v3 con seis demo y 116 activos. Catálogo
desplegado: 68 Published/34 con audio real. Los dos demos están en la activa y
no les afecta ese residual. El acceso propio por bastión falló; la fuente actual
es la consulta dbops recibida, no el handoff. No se afirma paridad ni cero desplegado.
[Residual y SQL reversible preparado](RESIDUAL-DESPLEGADO-3.21.md), **sin ejecutar**.
C1 editorial, UI 4.11 y aceptación administrativa conservan sus límites.
Sin push, cambios de producto, migraciones, specs ni `tasks.md`.
