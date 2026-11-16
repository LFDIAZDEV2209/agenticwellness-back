# Simulación de reparación 1.14 — §5.1

**1.14 es la reparación reversible de huecos, con dependencias 1.9/1.10b; no es
la carga de mp3.** Solo puede escribir ítems `program`, conservando individuales,
datos cerrados y la fixture `0c487f75-a462-4fda-894d-61c031441884`.

## Simulación local del 15-11-2026

Consulta real de Application, `EffectiveDayContentStore`,
`EffectiveDayContentResolver` y `GetContentGapsQueryHandler` de e0a9c29.
Transacción PostgreSQL **READ ONLY / REPEATABLE READ**, revertida al terminar;
sin host HTTP ni llamadas externas. Captura **19:10:05 America/Bogota**.
Salida y verificaciones: [evidencia versionada](evidence/1.14/README.md).

| Medida | Resultado |
| --- | ---: |
| Inscripciones activas | 300 |
| Días restantes evaluados | 59.787 |
| Días con hueco según 1.10b | **50.960** |
| Huecos podcast / ejercicio / nutrición | **50.960 / 0 / 0** |
| Días evaluados de la fixture protegida | 77 |
| Días con hueco de la fixture protegida, excluidos de escritura | **54** |
| Días con hueco en semanas futuras Locked elegibles fuera de la fixture | 50.838 |
| Otros días excluidos por inicio/estado/historial | 68 |
| Huecos preservados por individual | 0 |
| Semanas futuras Locked con huecos | 8.491 |
| Días reparados por el contrafactual de materializar program futuro | **0** |
| Actualizaciones de BD propuestas/ejecutadas | **0 / 0** |

## Por qué no se ejecutó una escritura ni una reversa ficticia

El código actual para podcast resuelve el snapshot individual válido, después
la plantilla semanal y la genérica; sin fuente válida devuelve `missing`.
Un `program` de snapshot solo conserva precedencia si la semana ya está
iniciada/activada/completada. En una futura Locked el contenido se vuelve a leer
desde plantilla, decisión explícita de actualización de futuras.

La simulación recorrió las mismas fechas que 1.10b. Como diagnóstico adicional,
insertó **solo en memoria** un podcast publicado válido en cada una de las
50.838 celdas futuras elegibles, con origin=program, preservando individuales.
El mismo resolvedor siguió devolviendo hueco en todas. No se presenta esa
selección arbitraria del catálogo como una propuesta clínica ni editorial.
Ninguna de esas inserciones llegó a PostgreSQL.

Materializar una fuente que §5.1 no selecciona no repara el dato efectivo.
Modificar origen a individual, activar semanas para obtener precedencia,
dar por válidos medios demo, cambiar el catálogo/plantilla, excluir filas de
1.10b o alterar su total fabricaría un cero fuera del alcance autorizado.

Además, los **54 días con hueco de la fixture protegida** impiden un cero global
mientras se exige conservarla y no cambiar fuentes compartidas que la afecten.
No se retiró esa fixture de la consulta para ocultar esos días.

**Estado: simulación completada, ejecución bloqueada por falta de fuente efectiva
y conflicto entre cero global y fixture protegida.** No se ejecutó apply sin
una reparación válida y no se afirma haber probado reversa con cero cambios.
Queda por definir una fuente de respaldo compatible con §5.1 y el alcance de la
verificación del cero; requiere una decisión explícita del owner.

## Reproducción

```powershell
# COP_PROGRAM_AUDIT_CONNECTION se resuelve solo en memoria desde configuración local.
dotnet run --project scripts/ProgramContentAudit -p:OutputPath=bin/Repair114/ -- --simulate-repair <ruta-simulation.json>
dotnet run --project scripts/ProgramContentAudit -p:OutputPath=bin/Repair114/
```

El segundo comando conserva la auditoría original 1.10b. La opción nueva no
es un apply y no introduce una API de reparación. Verifica host local y usa
guard administrativo; no expone pacientes ni credenciales en la salida salvo
el UUID técnico de la fixture excluida. La cancelación se propaga a consultas,
recorrido y escritura del informe. La fuente y el informe se versionan junto
con esta nota; no se altera la implementación de resolución del producto.

Los checksums de `0c487f75`, comida/cierre rev7, 33 R7 y las siete publicaciones/
cuatro cierres originales de QA4.11 permanecen iguales. No se aplicaron
migraciones, no se reinició el stack ni se escribió en catálogo, plantilla,
semanas o historia clínica durante esta simulación.
