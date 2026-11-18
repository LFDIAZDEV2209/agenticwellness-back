# Control de medicamentos: contrato del circuito inicial

Entrega local en desarrollo. Global `Medications:Enabled` tiene default `false`; no se modifica el contrato de login, perfil, nutrición ni del build 67. No hay envío de notificaciones en este circuito.

## Acceso

`GET /api/v1/me/capabilities` es aditivo. Respuesta:

```json
{"policyVersion":"medications-v1:1","modules":{"medications":{"enabled":true,"canView":true,"canManage":true,"canManageAccess":false}}}
```

Para pacientes se combina el flag global con el estado activo del paciente y las excepciones persistidas (`app.patient_module_access`, módulo `medications`). Sin excepción, el acceso está habilitado (`enabled=true`, `revision=0`); una excepción explícita `enabled=false` lo restringe y conserva su revisión. Un paciente inactivo o eliminado no obtiene acceso aunque tenga una excepción habilitada. No se crean filas de acceso durante las lecturas ni se requiere activación manual inicial. Un paciente habilitado sin tratamientos ve el estado vacío y puede registrar uno. La identidad se resuelve por JWT (`aud=app`) y `patient_profiles.user_id`. El cliente nuevo muestra Medicamentos en la tarjeta de Inicio y conserva Perfil en la barra inferior; si el módulo no está habilitado o el servidor responde 404, presenta un estado no disponible sin acceder a datos ni permitir escrituras. Perfil sigue accesible desde el menú de Inicio y la barra inferior. El cliente antiguo no necesita consultar capacidades.

Para el ERP, permisos `Medications.View`, `Medications.Manage` y `Medications.ManageAccess` más ámbito de paciente/clínica/profesional existente. El flag global desactiva todo el módulo sin cambiar permisos de otros endpoints.

## Endpoints

| Método/ruta | Resultado |
| --- | --- |
| GET `/api/v1/me/medications` | Workspace propio, admite `date=YYYY-MM-DD` y `timeZone=America/Bogota` para Hoy |
| POST `/api/v1/me/medications/treatments` | Crear tratamiento propio, devuelve tratamiento (201; reintento idéntico 200) |
| POST `/api/v1/me/medications/doses/{doseId}/events` | Registrar toma/omisión, devuelve dosis actualizada |
| PUT `/api/v1/me/medications/treatments/{id}/status` | Pausar/finalizar/reactivar con revisión esperada |
| GET `/api/v1/medications/overview?timeZone=America/Bogota` | Resumen ERP del ámbito autorizado: pacientes, habilitación, tratamientos activos y siete días de tomas/omisiones |
| GET `/api/v1/medications/patients?query=&page=1&pageSize=20` | `{items,total,page,pageSize}`; items `{patientId,name,mrn,enabled,activeTreatments}` |
| GET `/api/v1/medications/patients/{patientId}` | Workspace del paciente autorizado; mismos parámetros de fecha/zona |
| PUT `/api/v1/medications/patients/{patientId}/access` | `{enabled,expectedRevision?}`; devuelve `{enabled,revision}` |
| POST `/api/v1/medications/patients/{patientId}/treatments` | Crear desde ERP con procedencia de personal |
| POST `/api/v1/medications/patients/{patientId}/doses/{doseId}/events` | Registrar/corregir desde ERP con permiso de gestión |
| PUT `/api/v1/medications/patients/{patientId}/treatments/{id}/status` | Cambiar estado desde ERP |

## Workspace

```ts
type MedicationWorkspace = {
  patient: { id: string; name: string; mrn: string };
  access: { enabled: boolean; revision: number };
  treatments: MedicationTreatment[];
  doses: MedicationDose[]; // Día solicitado, ordenadas por scheduledAt.
  history: MedicationEvent[]; // Últimos 100 eventos, descendentes.
  summary: { activeTreatments: number; scheduledDoses: number; takenDoses: number; pendingDoses: number };
};
type MedicationTreatment = {
  id: string; medicationId: string | null; name: string;
  productType: 'medication' | 'vitamin' | 'supplement';
  status: 'active' | 'paused' | 'completed';
  origin: 'patient_reported' | 'staff_recorded' | 'legacy_import';
  revision: number; createdAt: string;
  dosage: number; unit: string; route: string; instructions: string;
  times: string[]; timeZone: string; startDate: string; endDate: string | null;
};
type MedicationDose = {
  id: string; treatmentId: string; name: string; dosage: number; unit: string;
  scheduledAt: string; timeZone: string;
  status: 'pending' | 'taken' | 'omitted' | 'cancelled';
  occurredAt: string | null; revision: number;
};
type MedicationEvent = {
  id: string; doseId: string; treatmentId: string; name: string;
  status: 'taken' | 'omitted'; occurredAt: string; recordedAt: string;
  reason: string | null; origin: 'patient_reported' | 'staff_recorded';
};
```

## Escrituras

Alta de tratamiento (las fechas son fecha civil ISO; los horarios son `HH:mm`):

```json
{"clientRequestId":"uuid","medicationId":null,"name":"Producto registrado","productType":"medication","dosage":1,"unit":"tableta","route":"oral","instructions":"Indicaciones de la receta existente","times":["08:00","20:00"],"timeZone":"America/Bogota","startDate":"2026-11-17","endDate":null}
```

Evento:

```json
{"clientRequestId":"uuid","status":"taken","occurredAt":"2026-11-17T13:00:00Z","reason":null,"expectedRevision":0,"scheduledAt":"2026-11-17T13:00:00Z"}
```

`scheduledAt` identifica la ocurrencia proyectada y debe enviarse al registrar una dosis que todavía no fue persistida. El servidor verifica su UUID determinista contra la pauta, vigencia y estados históricos; no acepta un horario arbitrario. GET es una lectura pura y no crea dosis ni eventos. Solo la escritura autorizada materializa la dosis.

`occurredAt` puede omitirse para usar hora del servidor. Reintentar la misma operación no duplica efectos; reutilizar su clave con otro contenido devuelve 409. Una segunda confirmación idéntica de una dosis ya tomada devuelve el estado actual sin crear otro evento, pero reserva también su clave de operación y hash; esa clave no puede reutilizarse sobre otra dosis. Cambiar un estado ya registrado requiere revisión esperada y motivo. Las dosis futuras/canceladas no se confirman.

Cambio de tratamiento: `{status:"paused",expectedRevision:1}`. Conserva eventos anteriores y registra el estado efectivo por instante. Una ocurrencia programada durante una pausa permanece cancelada tras reactivar, aunque el día no se hubiera consultado antes. Las ocurrencias futuras se proyectan con el nuevo estado; los eventos de toma existentes no se reescriben.

Validación: nombre 1–200, cantidad positiva, unidad 1–40, vía hasta 80, indicaciones hasta 2000, 1–8 horarios únicos válidos, zona horaria válida, fin no anterior al inicio, UUID de operación no vacío. No se transforma frecuencia libre antigua en horarios ni se generan recetas automáticamente.

## Compatibilidad y persistencia

Nuevas entidades de tratamiento, pauta versionada, ocurrencia y eventos tienen identidad propia; no dependen de los UUID reemplazables de `patient_medications`. El guardado antiguo del perfil debe conservar su lista y los nuevos tratamientos/tomas. Migraciones solo aditivas. La infraestructura E2E usa PostgreSQL local aislado, identidad de pruebas y transportes externos desactivados.

El primer recorrido funcional es entrar con un paciente activo sin excepción de acceso, registrar tratamiento en app, confirmar dosis, consultar el mismo registro persistido en ERP, reintentar y guardar el perfil con el contrato del build 67 sin perder historial. Se cubren accesos a otro paciente, otra clínica y lectura sin gestión. Existencias, síntomas, recordatorios, foto e interacciones siguen como entregas posteriores del plan.


## Inicio del ERP

El resumen usa el mismo filtro de paciente/clínica/profesional que el directorio,
antes de contar. La ventana incluye hoy y los seis días anteriores en la zona
solicitada; todas las fechas se devuelven como fecha civil. `activity` contiene
siete elementos `{date,taken,omitted}` e incluye días sin registros con ceros.
Cuenta dosis distintas en su último estado/`occurredAt`, no todos los eventos de
corrección. No se presenta ese dato como porcentaje de adherencia ni se infieren
tomas pendientes de las pautas libres antiguas. GET no materializa ocurrencias.

Respuesta: `{totalPatients,enabledPatients,patientsWithActiveTreatments,
activeTreatments,takenToday,periodStart,periodEnd,activity}`. Requiere ERP,
`Medications.View` efectivo y flag global. Los contadores son globales al ámbito;
no dependen de la página o búsqueda del directorio. No hay registros de otros
pacientes o clínicas en la respuesta. El ERP distingue un error de datos de un
resumen vacío y muestra accesos reales a alta, seguimiento e historial.

### Protocolo diario según acceso efectivo

El snapshot añade `optionalTaskCodes: ["nutraceutico"]` con `Medications:Enabled=true` y acceso efectivo habilitado para el paciente, incluido el default sin excepción. Sin acceso se omite el campo y se conserva el comportamiento anterior. El contrato y el enum de tareas no cambian. Para cuentas habilitadas el bonus diario y el umbral de racha se calculan con las misiones requeridas, excluyendo nutracéutico; las completaciones/XP anteriores y el endpoint legado se conservan. Registrar una dosis de medicamentos no concede XP ni crea una completación de programa. El cliente actual representa los suplementos como un adicional del mismo workspace de dosis, sin doble registro.

El contador `enabledPatients` incluye pacientes activos del ámbito sin restricción explícita, aunque no tengan fila de acceso ni tratamientos. Los pacientes inactivos no cuentan como habilitados. Cambiar el acceso no pausa tratamientos ni elimina historial. No se requiere migración o backfill para el nuevo default.
