# Borrador de cierre 3.21 — pendiente de aprobación del owner

**Actualización posterior:** la [reparación de `default-83w`](REPAIR-1.14-LEGACY.md)
dejó 1.10b global en **0**, con reversa probada. La excepción de 50.322 huecos
propuesta más abajo es histórica y ya no es necesaria para ese corte. Este
documento no sustituye la aprobación del cierre por el owner ni acredita C1/UI.

**No constituye cierre ni declaración de cero.** Luis autorizó la reparación D
de la plantilla activa; no ha aprobado una excepción al criterio global de 3.21.

Se propone aceptar el cierre técnico de las verificaciones F3 con la excepción
explícita de 1.14 que se describe abajo. La aceptación administrativa requiere
la decisión expresa del owner y su responsable/plazo de seguimiento.

## Evidencia disponible

- 3.20 real: recorrido clínico completo y cierre revisión 7, con preservación de
  33 R7. Evidencia compartida `revision-3.20-real/README.md`; referencias de esa
  captura: ERP a7d66fe, APP cc093ac y API 1a14083. No se mezcla con pruebas F4.
- 3.21 F3: sobre eb181a8, build 0 errores/15 avisos, 2630 unitarios y 105 omitidos,
  PostgreSQL 119/119; evidencia `revision-3.21-back/README.md`. Es la captura
  previa a la integración final del fix de imagen, no una repetición sobre F4.
- F4 e0a9c29 integra ese fix: build aprobado, 2801 unitarios/105 omitidos,
  PostgreSQL 157/157 y smoke final 27 peticiones/96 aserciones, incluido metadata
  autorizado. Evidencia histórica `revision-4.12-back`.
- 4.11 back contra e2e front 9d62b7e: [API/DB y degradación](evidence/4.11/validation-9d62b7e-readonly/README.md),
  con límites explícitos. Los tres casos UI siguen omitidos por rediseño.
- 1.14 opción D: [operación reversible y A2/F2](REPAIR-1.14-D.md).

## Excepción propuesta de 1.14

1.10b global, sin excluir `0c487f75`, deja **50.322 días con hueco**, todos podcast,
en 116 inscripciones de la plantilla archivada `default-83w` (83 semanas).
La plantilla activa de 83 días queda con **0 huecos** en 186 inscripciones y la
fixture protegida con 0 efectivos; sus filas/cierres no se tocaron.
La reparación acotada elimina 697 días con hueco en el corte de reversa/verificación.

El catálogo de 13 audios reales verificados cubre las 83 celdas y siete respaldos
genéricos; se mantiene la desviación temporal C1 frente a la base de 42 publicada
solicitada. Faltan 24 MP3 del catálogo actual de 37 y no se afirma que su llegada
por sí sola satisfaga el número original 42.

Aceptar este borrador implica aceptar temporalmente el residual de la plantilla
archivada y asignar su tratamiento editorial/migración a un alcance posterior.
No autoriza cambios en datos cerrados, migraciones de inscripciones, exclusiones
de la consulta ni alteración de §5.1. A2/F2 está corregido/probado en fuente y BD
aislada; el runtime compartido sigue e0a9c29 hasta un despliegue autorizado.

**Decisión pendiente:** aceptar/rechazar la excepción, designar responsable y
fecha de revisión. Hasta entonces, 3.21 conserva estado pendiente de aprobación;
no se actualiza tasks.md ni se publica nada.
