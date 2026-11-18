# Control de medicamentos: circuito manual

Circuito compartido por backend, ERP y app. `Medications:Enabled` es el
interruptor operativo del entorno (default de configuración `false`).

## Alcance

- App: acceso a Control de medicamentos en lugar de la tarjeta Mi perfil en Inicio; Perfil permanece en la barra inferior. Los pacientes activos tienen acceso por defecto, salvo restricción explícita. Nutracéutico sigue separado.
- Tratamientos registrados manualmente por paciente o personal: nombre/catálogo, tipo, cantidad/unidad, vía, indicaciones existentes, horarios, zona y vigencia.
- Hoy: registro de toma/omisión, historial atribuible y correcciones con motivo/revisión. Pausa, reactivación y finalización conservan los estados efectivos.
- ERP: directorio acotado, restricciones/restauración de acceso por paciente, mismo seguimiento persistido y accesos desde paciente/Perfil 360.
- ES/EN, estados vacíos/errores, lectura sin gestión y temas claro/oscuro del ERP.

No crea prescripciones ni recomienda dosis. Suministros, síntomas, notificaciones,
foto, interacciones, exportaciones y extensión de asignaciones a otros módulos
permanecen en las fases posteriores del [plan](../../plans/medications-erp-app.md).
Perfil 360 ofrece un enlace al workspace, sin duplicar allí el detalle clínico.

## Acceso

El flag global es el interruptor del despliegue. Con el módulo habilitado, un
paciente activo y no eliminado tiene acceso sin crear una fila en
`patient_module_access`: ausencia de excepción significa `enabled=true`,
`revision=0`. Una fila explícita `enabled=false` sigue restringiendo el acceso;
no se sobrescribe con el nuevo default. Restaurar acceso requiere
`Medications.ManageAccess`. Un paciente inactivo sigue sin acceso, incluso con
una excepción `enabled=true`.

`MedicationAccessQuery` resuelve esta política para capacidades, workspace,
directorio, contador `enabledPatients` y programa. Las lecturas no crean
activaciones. La restricción no pausa tratamientos ni borra eventos; al
restaurar el acceso se conserva el historial. No tener tratamientos muestra
un estado vacío en la app.

Los permisos del personal son un control distinto.
`Medications.View`, `Medications.Manage` y `Medications.ManageAccess` utilizan el
contexto efectivo de clínica/organización y el ámbito profesional; un grant
acotado no necesita aparecer como permiso global en el JWT. El servidor vuelve
a comprobar identidad, aplicación, paciente y contexto en cada operación.

App sin acceso, fallo de capacidades o backend antiguo con 404 muestra Medicamentos no disponible, sin consultas de tratamientos ni escrituras.
No hay cola offline ni escrituras clínicas en almacenamiento local.

## Base de datos

Ocho tablas nuevas en el mismo PostgreSQL/`AppDbContext`, bajo `app`:

| Tabla | Propósito |
| --- | --- |
| `medication_treatments` | Identidad estable y procedencia del tratamiento |
| `medication_regimens` | Pauta versionada |
| `medication_schedule_slots` | Horarios locales estructurados |
| `medication_doses` | Ocurrencias materializadas al confirmar |
| `medication_intake_events` | Registro de toma/omisión y cadena de correcciones |
| `medication_treatment_status_events` | Estado efectivo por instante |
| `medication_dose_operations` | Claves y hashes de operaciones aceptadas, incluidos no-ops |
| `patient_module_access` | Excepciones al acceso predeterminado por paciente |

Migraciones aditivas: `20261117190158_AddMedicationControl` y
`20261117192114_AddMedicationLifecycleAndOperations`. Deben entregarse juntas.
Incluyen FK restrictivas, unicidad, revisiones, restricciones de estado y ocho
registros de triggers de auditoría. No convierten la frecuencia libre anterior
ni mueven/borran datos de `patient_medications`. El catálogo sigue siendo común;
ninguna tabla nueva depende del ID del vínculo legacy paciente/medicamento.

GET proyecta las dosis del día sin persistirlas. Su UUID determinista se verifica
contra la pauta al confirmar, y solo esa escritura crea la dosis y su evento.
Las pausas se resuelven por el estado efectivo en el horario programado; visitar
un día antes o después de reactivar no cambia su historia. Las transacciones y
los reintentos se describen en [transactions.md](../../database/transactions.md).
El [contrato HTTP](contracts.md) documenta `scheduledAt`, revisión e idempotencia.

El cambio a acceso predeterminado no necesita migración ni backfill. No modifica
filas existentes, permisos, tratamientos, tomas o XP. Se aplica al leer la
política y conserva las restricciones explícitas. La consulta usa el índice
único `(patient_id,module_code)` y la PK de pacientes; el contador sigue siendo
una consulta del estado actual del ámbito, sin un nuevo agregado histórico.

## Compatibilidad con Apple 67

Base identificada: versión 1.0, build 67, commit
`c542af2e0aa60644b833496debfbe8918f93d603`, ejecución GitHub
[43295056878](https://github.com/FontalvoB/antares-paciente/actions/runs/43295056878).
El número de ejecución es 92; el log del IPA/subida es la evidencia del build 67.

No se modificaron login, estructura de GET/PUT del perfil, archivos nativos ni
configuración de Apple. El guardado propio ahora actualiza solo los escalares
que recibe; la edición general del ERP reconcilia la lista legacy conservando
IDs/fechas de los registros existentes. Los tratamientos/eventos nuevos tienen
identidad independiente de ambas pantallas.

La integración local verifica el cuerpo GET/PUT extraído del commit congelado y
la conservación de tratamiento, toma, historial e ID legacy después de guardar.
También ejecuta el guardado desde la UI actual de Perfil. Esto no equivale a
haber ejecutado el IPA/TestFlight 67 en un iPhone. Esa prueba física y la revisión
del entorno candidato siguen siendo el gate antes de habilitar producción.

## Verificación reproducible

- App: `docs/MEDICATIONS.md` y `e2e/medications-ui.mjs` en antares-paciente.
- ERP: `playwright.medications-ui.config.ts` / `e2e/medications-ui.spec.ts`.
- Integración real: [host de pruebas](../../../tests/CoppAddresd.Medications.E2EHost/README.md), `playwright.medications-real.config.ts` y `e2e/medications-cross-app-real.spec.ts` en ERP.
- Default habilitado: `e2e/medications-default-access-real.spec.ts` en ERP verifica app sin activación, estado vacío, alta/toma sin override, contador/directorio/programa coherentes, restricción explícita con permiso, restauración con historial y denegación de pacientes inactivos.
- Regresiones backend: namespace `CoppAddresd.UnitTests.Medications`, `PatientProfilePromotionTests` y `PatientMedicationIdentityTests`.

Las pruebas UI sintéticas establecieron RED antes de implementar las pantallas;
la integración independiente usa Identity/JWT, API, Auth y PostgreSQL reales,
sin interceptar las APIs clínicas. Transportes SMS, WhatsApp, SOS y proveedores
externos permanecen desactivados. Los cuatro defectos hallados en revisión
(scoped grants, GET con escrituras, periodo pausado y clave no-op reutilizable)
tienen regresiones E2E reales con RED previo a la corrección.

Evidencia local: `/tmp/copp-medication-e2e`,
`/tmp/copp-medications-app-ui-final`, `/tmp/copp-medications-visual`,
`/tmp/copp-medication-upgrade`. Los resultados finales deben leerse en esos
informes; una captura sintética no acredita persistencia ni entrega a dispositivos.


## Resultados locales del 17/11/2026

- Integración real: 7/7 recorridos correctos en 21,7 segundos; `green-final.log`.
- UI app: 11 escenarios; UI ERP: 8 escenarios, correctos tras RED inicial.
- Backend: 443 regresiones de medicamentos/perfil/alcance correctas; 2 regresiones
  PostgreSQL de identidad legacy correctas tras RED.
- Build app y ERP, TypeScript, lint enfocado e i18n sin claves faltantes correctos.
- Clon restaurado: se aplicaron exclusivamente las dos migraciones candidatas.
  Las 192 tablas previas conservaron su esquema; las 191 tablas que no son el
  historial de migraciones conservaron filas y huellas SHA256. El historial añadió
  solo dos entradas. Las ocho tablas nuevas quedaron vacías. Down/Up reprodujo
  exactamente el estado esperado. Informe: `/tmp/copp-medication-upgrade/validation-report.json`.
- El backup anterior tiene 30 migraciones previas pendientes que se excluyeron
  de ese ensayo acotado. No acredita el efecto conjunto de aplicarlas todas ni
  sustituye un ensayo de actualización del entorno real antes de desplegar.
- La tabla legacy del backup estaba vacía; la conservación de IDs con filas se
  probó aparte con los datos sintéticos de integración real.
