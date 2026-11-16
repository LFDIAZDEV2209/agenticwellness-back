# Preflight de release backend — merge de dev

Ejecución local del 15–16 de noviembre de 2026. **Merge completado; preflight bloqueado por 15 fallos PostgreSQL legados.** No se declara aptitud de release con esos fallos abiertos.

## Merge y conservación

- Rama: `org/f4-coppAddresdBack`.
- `git fetch origin` completado antes del merge.
- Padre F4: `9b901d710e8a575e404c2247370f5a67960867f0`.
- `origin/dev` incorporado: `f1091a4bb6faa39818f99336011ad676fce9d999`.
- Merge mediante `--no-ff --no-commit`, resolución y commit: **`a79bbaebfe94241283e1522caed791716c6180f6`**. Sin rebase ni push.
- Conservados F4, arranque A2/F2, reparaciones 1.14-D/legacy y contrato 2.22-back; incorporados los cambios de dev, incluidos Vita, voz/chat y outbox SOS. Nueve archivos críticos de F4/1.14/2.22 son idénticos al padre F4; detalle verificable en `merge.json`.

Hubo **15 archivos con conflicto**. Se conservaron los hunks F4 que ampliaban el contrato, junto con los cambios de dev fuera de esos hunks. No se sustituyeron archivos completos por una rama:

| Archivos | Resolución |
| --- | --- |
| `docs/database/indexes.md` | Unión de índices F4 y documentación SOS. |
| `DAILY-ROUTINES.md`, `EFFECTIVE-CONTENT.md`, `FASE1-VALIDACION.md` | Conservación de la documentación ampliada F4. |
| `ProgramProgressDtos.cs` | Conservación de `Upcoming` y descanso opcional. |
| `GetMyNutritionPlanQuery.cs` | Conservación de versión y visibilidad por continuidad/timeline. |
| `EffectiveDayContentResolver.cs`, `EffectiveDayContentStore.cs` | Conservación del resolver canónico ampliado F4. |
| `DependencyInjection.cs` | Registro 2.22 conservado; registros SOS de dev incorporados. |
| `ProgramRepository.cs` | Reloj y estado `Upcoming` conservados; parcial Vita de dev incorporado. |
| Designers de `AllowThirtyDayLegacyNutritionCompletions` y `AddExerciseSessionDetailSnapshot` | Modelo F4 más metadata acumulativa SOS. |
| `EffectiveDayContentPostgresTests.cs`, `ExerciseSessionSnapshotTests.cs`, `EffectiveDayContentResolverTests.cs` | Fechas deterministas, destino correcto de reversa y casos adicionales F4. |

Las rutas completas y los hunks se encuentran en [resolutions.json](./evidence/release-preflight/resolutions.json) y [conflicts.json](./evidence/release-preflight/conflicts.json).

## Migraciones

| Contexto | F4 previo | dev | Merge |
| --- | ---: | ---: | ---: |
| AppDbContext | 112 | 104 | **113** |
| AuthDbContext | 16 | 16 | 16 |
| CommunityDbContext | 21 | 21 | 21 |
| TelemedicineDbContext | 16 | 16 | 16 |
| WhatsAppDbContext | 5 | 5 | 5 |

No hay IDs duplicados por contexto. Las 161 migraciones compartidas tienen el mismo hash de operaciones; no se renombraron IDs ni se modificaron `Up`/`Down`. Dev aporta `20261113195741_AddSosWhatsAppOutbox`; F4 aporta nueve migraciones exclusivas de contraindicaciones, ciclos nutricionales y briefs IA. Inventario completo: [migration-branches.json](./evidence/release-preflight/migration-branches.json).

El snapshot final contiene ambos modelos. Se incorporó la metadata SOS a los **11 target models posteriores** de F4 para que cada modelo histórico siga siendo acumulativo. Los 12 modelos desde la migración SOS incluyen esa entidad. Esto corrige la coherencia histórica sin cambiar operaciones ni historial aplicado.

La prueba nueva `ReleaseMerge_MigracionesNuevasYSaltosDesdeAmbasRamas_NoColisionanNiPierdenModelo` pasó en PostgreSQL aislado:

1. Instalación desde cero: 113 aplicadas, ninguna pendiente y ningún cambio de modelo pendiente.
2. Estado reconstruido con las 112 IDs F4: pendiente únicamente SOS; upgrade a 113 conserva el dato centinela F4.
3. Estado reconstruido con las 104 IDs dev: nueve pendientes F4; upgrade a 113 conserva el centinela, tabla SOS y longitud 200 de `model_name`.

Son reconstrucciones de esquema/historial dentro de una BD efímera, mediante operaciones reales de migración y ajuste de la diferencia previa de contraindicaciones/SOS; no despliegues de las aplicaciones antiguas. El test exige el prefijo `copp_nutrition_test_` antes de modificar el historial. Se ejecutó solo y también dentro de la suite principal. [Resultado de la matriz](./evidence/release-preflight/migration-runtime.json).

La BD local compartida se consultó **solo en lectura**: historial App con 113 IDs conocidas, cero pendientes y cero desconocidas. No se aplicaron DDL ni cambios de historial allí. No se reiniciaron servicios ni se modificaron pacientes, fixture protegida o datos cerrados.

## Gates sobre a79bbae

| Gate | Resultado |
| --- | --- |
| Build de la solución | **PASS**, 0 errores, 15 advertencias. |
| Unitarios principales | **PASS**, 2997 pasados, 105 omitidos por requerir PostgreSQL. |
| Unitarios Telemedicina | **PASS**, 413/413. |
| Integración principal PostgreSQL completa | **PASS**, 223 pasados, 1 omitido. Incluye F4, 2.22 y matriz de migraciones. |
| Community PostgreSQL | **PASS**, 98/98. |
| Telemedicina PostgreSQL | **PASS**, 44/44. |
| WhatsApp, incluidos casos PostgreSQL | **PASS**, 103/103. |
| Activación adicional de los 105 casos PostgreSQL del proyecto UnitTests | **FAIL**, 90 pasados, 15 fallidos. |
| Misma selección de 105 en assembly previo e0a9c29 | **FAIL**, 90 pasados, los mismos 15 fallidos con idénticos mensajes. |

La única omisión de integración principal es `ClienteReal_ContratoBilingueY503DeProveedor`: requiere `COP_PROGRAM_NARRATIVE_SIM_URL`. No se contabiliza como validación de ese proveedor.

Se reutilizaron los harness del repositorio, salida separada `bin/Validation114D` y conexiones en memoria de proceso obtenidas del helper local; las suites crean sus BD aisladas. Los unitarios sin conexión se ejecutaron primero; los 105 omitidos se seleccionaron por nombre y se ejecutaron después con PostgreSQL. La comparación usa el assembly cacheado de `e0a9c29` en `bin/Validation412`, con hash registrado y fuentes relevantes verificadas idénticas. No se compilaron ni desplegaron cambios sobre el stack compartido.

### Bloqueo legado reproducido

La igualdad de los 15 nombres y mensajes está verificada automáticamente en [legacy-failures-comparison.json](./evidence/release-preflight/legacy-failures-comparison.json). Prueba que el conjunto observado ya se reproduce antes del merge; no sustituye resolver los fallos.

- `Plantillas_UpsertCreaActualiza_YReemplazoDeTareas`: espera 3 y obtiene 4.
- `TareaEmocional_MigracionReversible_ConservaRegistrosAnteriores`: asume que la migración emocional sigue siendo la última.
- Tres casos `BonusDayBonus_*`: restricción FK al borrar reglas XP usadas por ledger.
- `Racha_Hito14_*`: espera 300 y obtiene 600.
- `Snapshot_JsonShape_CumpleSpec71`: fecha esperada del 16–22 y fecha obtenida del 15 de noviembre.
- Dos casos `CompleteTask_*` de autorización: 422 en lugar de 200/409.
- Tres casos de autorización/listado: 499 en lugar de 200/403.
- Dos casos clínicos de scores: 404 en lugar de 200/422.
- `InventoryMetricsProcessorTests.EntryCreated_UpdatesEntriesCount`: espera 1 y obtiene null.

Hay indicios de supuestos antiguos de fixtures, fecha, alcance y estado compartido entre casos; no se presenta esa hipótesis como diagnóstico cerrado. No se relajaron permisos, asserts ni código de producto para obtener verde. El gate queda pendiente de corregir esas pruebas o de una decisión explícita sobre su tratamiento antes del release.

## Evidencia

[MANIFEST.json](./evidence/release-preflight/MANIFEST.json) registra hashes SHA-256 de la evidencia versionada. Los JSON `*-results.json` se derivan de cada TRX e incluyen todos los nombres, outcomes y duraciones; los fallidos incluyen mensaje y stack. Los logs y TRX crudos permanecen en `openspec/changes/organizar-experiencia-bienestar/revision-release-preflight-back` del workspace compartido, con hash y tamaño en `raw-artifacts.json`. Se versionan los resúmenes y logs acotados para evitar incorporar megabytes de trazas SQL de fixtures.

No se modificó `tasks.md`, no hubo push, no se reinició el stack ni se reaplicó la reparación 1.14 sobre datos compartidos durante este preflight.
