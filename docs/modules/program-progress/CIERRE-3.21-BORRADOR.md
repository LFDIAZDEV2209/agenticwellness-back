# Borrador de cierre 3.21 — pendiente de aprobación del owner

La [verificación actual](VERIFICACION-3.21.md), sobre `9a59f339`, confirma
**1.10b local global = 0**, podcast/ejercicio/nutrición **0/0/0**, sin exclusiones:
175 inscripciones activas, 59.778 días evaluados. Incluye 116 inscripciones de
`default-83w` archivada y la fixture protegida `0c487f75`, con cero huecos efectivos.
La continuidad nutricional se acredita en **590 días / 3 inscripciones**, todos
con el menú final 30 propio y **0 inválidos**.

Este resultado sustituye la propuesta histórica de aceptar **50.322 días con
hueco local** como excepción. Esa excepción ya no es necesaria para este corte.
Los antecedentes y la reversa real local permanecen en
[REPAIR-1.14-LEGACY](REPAIR-1.14-LEGACY.md); sus denominadores/fechas no se mezclan
con la nueva captura.

## Evidencia de fase

- 3.20 real: recorrido clínico y cierre revisión 7, preservando 33 R7;
  `revision-3.20-real/README.md` (ERP a7d66fe, APP cc093ac, API 1a14083).
  Es evidencia histórica de ese recorrido.
- F3 `eb181a8`: build 0 errores/15 avisos, 2630 unitarios/105 omitidos,
  PostgreSQL 119/119; `revision-3.21-back/README.md`. No se atribuyen al SHA actual.
- F4 `e0a9c29`: 2801 unitarios/105 omitidos, PostgreSQL 157/157 y smoke
  27 peticiones/96 aserciones; evidencia histórica `revision-4.12-back`.
- [Release-preflight](RELEASE-PREFLIGHT.md): integración/migraciones conjuntas;
  **15 fallos PostgreSQL legados** reproducidos también antes del merge.
  Esta tarea no los corrige ni declara todo el repositorio verde.
- [3.21 actual](VERIFICACION-3.21.md) y [gates/evidencia](evidence/3.21/README.md):
  consulta real, continuidad, build y suites ejecutadas en esta sesión.
- 4.11: [API/BD y degradación](evidence/4.11/validation-9d62b7e-readonly/README.md).
  Los casos UI pendientes por rediseño siguen fuera de la acreditación backend.

## Límites que requieren decisión

1. **Paridad desplegada pendiente.** La consulta real READ-ONLY a RDS vía ECS
   `cooppadresd-dbops:4` del 16-11, aportada por Luis, confirma seis demo en
   `default-83w` Archived v3 y 116 inscripciones Active; activa v4 con siete
   genéricas válidas y ninguna específica. Los dos demos están en la activa y no
   les afecta este residual. Esta fuente sustituye al handoff.
   [Residual y SQL reversible preparado](RESIDUAL-DESPLEGADO-3.21.md), **sin ejecutar**.
   No se afirma cero ni número actual de días con hueco en RDS.
2. **C1 editorial:** el antecedente local era 13 audios reales/37 episodios y base
   original de 42. RDS informa 68 Published/34 con audio real: inventario distinto,
   que no acredita por sí solo C1 ni permite trasladar los 24 MP3 pendientes locales.
3. **UI 4.11 y aceptación de fase:** no las sustituye la verificación backend.

El orquestador decide ejecutar la reparación desplegada tras obtener lectura,
respaldo y aprobación concreta. El owner conserva la aprobación administrativa,
responsables y plazos de los límites abiertos. **Sigue siendo un borrador;
no declara la fase cerrada ni acepta excepciones por cuenta del owner.**

Sin push, sin `tasks.md`, sin archivo/sincronización de specs ni escrituras en RDS.
