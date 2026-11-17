# Plan de medicamentos e integración entre ERP y app

Fecha: 17 de noviembre de 2026. Estado: propuesta de implementación; este documento no acredita funcionalidades entregadas.

Referencia: `erp-medicamentos.html`, adjunto del usuario. Revisión de código local: backend `852d75c8`, ERP `c0abb8f` y app `bf1f401`. El ERP también contiene cambios locales de presentación todavía sin publicar.

## Resultado esperado

Un módulo de **seguimiento de medicamentos**, integrado al ERP y a la app existente. El paciente registra lo indicado en su prescripción, sus tomas, existencias y síntomas; el personal autorizado consulta y da seguimiento desde el ERP. Se incluyen medicamentos, vitaminas y suplementos, con tipo explícito.

Conservar el alcance del adjunto: la plataforma registra tratamientos existentes; no genera recetas ni decide dosis. La información introducida por el paciente se identifica como reportada por él. Una foto o una extracción de texto no equivale a una prescripción verificada. No se introduce una aprobación clínica obligatoria para cada registro del paciente; si una organización necesita ese flujo, se configura como una capacidad adicional y se diferencia de la simple captura.

Un mismo producto permite habilitar módulos y entregar contenido distinto a cada usuario. La disponibilidad del módulo, los permisos de acción y las asignaciones de contenido son controles separados.

## Situación verificada

| Área | Base reutilizable | Trabajo faltante |
| --- | --- | --- |
| Backend | Pacientes, catálogo de medicamentos con NDC/RxNorm, permisos y contexto clínico, almacenamiento privado, auditoría de escrituras, transporte FCM y recibos de consentimiento | Tratamientos longitudinales, pautas estructuradas, ocurrencias de dosis, registros de toma, inventario del paciente, síntomas, alertas y auditoría explícita de lectura/exportación |
| ERP | Sesión, navegación por permisos, cliente HTTP, directorio de pacientes, Perfil 360 y consultas reales al catálogo | Módulo conectado de medicamentos, vistas de seguimiento y acciones autorizadas |
| App | Identidad de paciente, programa y contenido asignado, notificaciones y estructura de pantallas Ionic | Pantallas de medicamentos, capacidades por usuario, destinos de notificación y sincronización de tomas |

La auditoría general añadida en `852d75c8`/`c0abb8f` ya ofrece consulta de metadatos con `Audit.View`; reutilizar su visor y filtros. No incorpora nuevos eventos de lectura o exportación de medicamentos, que siguen pendientes.

El Recetario antiguo del ERP usa datos en memoria y su ruta está deshabilitada. No se reactiva como si fuera el nuevo módulo. La lista actual `PatientMedication` solo tiene referencia al medicamento, frecuencia libre y orden; al actualizar el paciente se reemplazan sus filas. El historial nuevo necesita identificadores propios estables.

El HTML tiene datos generados, permisos visuales sin control efectivo, exportaciones y avisos simulados, y una lista fija de interacciones sin proveedor definido. Su inventario global agrupa existencias **en poder de los pacientes**; no representa la bodega del ERP. El botón de efectos secundarios de Inicio tampoco tiene una vista `fx` en el mapa de renderizado del demo.

## Acceso y contenido por usuario

Crear una resolución de capacidades en el backend, compartida por ERP y app, con versión de política. Contrato propuesto: `GET /api/v1/me/capabilities`. La respuesta entrega módulos habilitados y acciones permitidas para la aplicación y el contexto actuales. Los contratos se cerrarán antes de escribir las pantallas.

La decisión combina:

1. Acceso a la aplicación (`erp` o `app`) y habilitación del módulo.
2. Permiso concreto para la acción.
3. Propiedad del paciente o alcance autorizado de clínica, organización y asignación profesional.
4. Publicación, asignación y elegibilidad del contenido solicitado.
5. Consentimiento y preferencias cuando la operación o el canal los requieran.

Cada API vuelve a comprobar estos requisitos. La respuesta de capacidades facilita la interfaz; no reemplaza la autorización. Un rol o un menú oculto no son un límite de acceso.

| Configuración | Qué determina | Ejemplo |
| --- | --- | --- |
| Módulos del usuario | Funcionalidades disponibles, con valores por defecto y excepciones autorizadas | Medicamentos y nutrición activos; comunidad desactivada |
| Permisos | Acciones permitidas sobre recursos y ámbitos autorizados | Consultar tratamientos, atender alertas, exportar |
| Plan/asignaciones | Contenido concreto que recibe el paciente | Su menú, su rutina, sus podcasts y sus materiales de tratamiento |
| Publicación y vigencia | Qué versión puede entregarse y durante qué período | Un borrador clínico nunca aparece en el contenido del paciente |

La administración necesita una sección de módulos/asignaciones por usuario o paciente, protegida por un permiso de gestión. Dar acceso a un módulo no concede acceso a otros pacientes. Los permisos del personal se asignan con las pantallas actuales de usuarios y roles; no se duplican en el nuevo módulo.

Plantillas iniciales propuestas:

| Perfil | Acceso previsto |
| --- | --- |
| Paciente | Sus tratamientos, sus tomas, sus existencias, sus síntomas y su contenido habilitado |
| Soporte clínico | Seguimiento de pacientes autorizados y gestión de alertas; cambios de pauta solo con permiso específico |
| Profesional autorizado | Revisión y registro/corrección de tratamientos dentro de su alcance, con autor y procedencia |
| Auditor | Lectura y auditoría del ámbito autorizado; exportar requiere permiso independiente |
| Administrador | Configuración y asignación de accesos; las operaciones clínicas conservan sus permisos y ámbito |

Familias de permisos propuestas: `Medications.View`, `Medications.Manage`, `Medications.Alerts.Manage`, `Medications.Inventory.View`, `Medications.Inventory.Manage`, `Medications.Export`, `Medications.Audit.View`, más acciones sobre datos propios para la app. Los nombres definitivos se incorporan al catálogo y se verifican con los contratos.

Mostrar u ocultar navegación, accesos de Inicio, acciones, búsqueda, destinos de notificación y herramientas/contexto del chat a partir de la misma política. Filtrar datos en el servidor antes de calcular totales, paginar o exportar. Las notas internas, borradores y fotos sin autorización no se devuelven al paciente ni se incorporan al chat.

Un módulo habilitado sin tratamientos muestra un estado vacío útil para registrar el primero. Eso se diferencia de un módulo no autorizado, que no se ofrece. Cambios de usuario, clínica o permisos invalidan el estado protegido y su caché. Definir una revisión de política o consulta vigente para no depender de permisos antiguos de un JWT hasta el próximo login. Los envíos pendientes también vuelven a comprobar elegibilidad antes de despachar.

Aplicar esta capa primero a Medicamentos y conectarla progresivamente con las asignaciones existentes de nutrición, ejercicios y podcasts, sin reconstruir esos módulos ni crear roles distintos para cada paciente.

## Experiencia del ERP

Integrar en el shell actual, con colores COPP, tema claro/oscuro y ES/EN. No añadir otro login ni una segunda barra lateral.

- **Resumen:** pacientes en seguimiento, adherencia calculable, alertas abiertas y existencias próximas a agotarse. Conteos limitados al ámbito del usuario.
- **Pacientes:** búsqueda/filtros y filas compactas; nombres de una línea con el texto completo accesible.
- **Ficha:** resumen, tratamientos, seguimiento por semana/día, existencias y síntomas. Entradas desde el directorio y Perfil 360.
- **Alertas:** asignación, atención y resolución diferenciadas. Atender una alerta no equivale a resolver un problema clínico.
- **Existencias de pacientes:** suministros y vencimientos, separados del inventario de bodega.
- **Auditoría e informes:** lecturas, cambios, exportaciones y acciones de seguimiento, según permiso.

No mostrar porcentajes de adherencia si todavía no hay dosis evaluables. Mantener carga, vacío, error y falta de permisos como estados diferentes.

## Experiencia de la app

Decisión del usuario: el nuevo módulo visible es **Nutracéutico** y **Medicamentos queda dentro de él**. Nutracéutico reemplaza la entrada de **Mi perfil** en los módulos de Inicio y el destino **Perfil** de la navegación inferior. La disponibilidad del nuevo módulo se resuelve con las capacidades del usuario.

Navegación inferior prevista: **Inicio · Nutrición · SOS · Chat · Nutracéutico**. Si el usuario no tiene acceso a Nutracéutico, no se ofrece ese destino; la navegación se ajusta sin exponer una pantalla no autorizada.

El perfil personal conserva sus pantallas y funciones de cuenta, accesibles desde el avatar/menú de usuario. El cambio reemplaza su posición de módulo principal; no elimina el código ni el acceso a datos personales, contactos y configuración.

Dentro de **Nutracéutico → Medicamentos**, la vista principal es **Hoy**. Desde ella se accede a **Mis medicamentos**, **Historial**, **Existencias** y **Reportar síntoma**. Integrar estos destinos al patrón de pantallas actual de la app, sin sustituir todo su sistema de navegación.

La lección actual `program-nutraceutico` puede enlazar al nuevo módulo, manteniendo separado su registro de actividad del programa: completar una lección no confirma una dosis ni modifica la adherencia de medicamentos.

1. **Mis medicamentos:** registrar por búsqueda/manual desde una prescripción existente; tipo, presentación, cantidad por toma, unidad, vía, indicaciones, horarios y vigencia.
2. **Hoy (inicio de Medicamentos dentro de Nutracéutico):** próximas tomas y acciones claras de registrar toma u omisión. La app muestra el resultado guardado por el servidor.
3. **Detalle e historial:** pauta registrada, registros y correcciones, con fecha real de toma y fecha de registro diferenciadas.
4. **Existencias:** carga inicial, reposición, ajustes y vencimiento del suministro propio.
5. **Reportar síntoma:** descripción, fecha, intensidad reportada y asociación opcional a un medicamento; el reporte no afirma causalidad.
6. **Contenido:** indicaciones registradas y materiales publicados/asignados que el paciente puede consultar. El chat recibe solo contexto autorizado y no modifica pautas.

Captura por foto: entrega posterior al circuito manual, como ayuda para precargar campos que el usuario confirma. Mantener siempre búsqueda/manual como alternativa.

## Modelo y contratos propuestos

La API será la fuente de verdad compartida. Resolver el paciente de la app desde el JWT; no aceptar un ID del cliente como prueba de propiedad.

| Recurso propuesto | Regla principal |
| --- | --- |
| Tratamiento del paciente | ID estable, referencia al catálogo cuando exista, procedencia, instrucciones originales, unidad/cantidad, vigencia y estado |
| Versión de pauta | Horarios, zona horaria, fechas efectivas y revisión; no reescribe dosis pasadas |
| Dosis programada | Ocurrencia identificable con copia de la pauta vigente al generarse |
| Evento de toma | Dosis, estado reportado, hora de toma, hora de recepción, actor, motivo e idempotencia; correcciones trazables |
| Suministro y movimientos | Existencias por paciente y lote/vencimiento, entradas, consumo, ajustes y correcciones |
| Reporte de síntoma | Asociación declarada, origen y seguimiento profesional separado |
| Alerta y entrega | Caso, estado, responsable e historial; intentos de notificación separados del registro de toma |
| Auditoría | Acciones atribuibles, incluidas lectura y exportación, sin copiar contenido clínico a logs generales |

Grupos de endpoints nuevos propuestos:

- `/api/v1/me/medications`: tratamientos propios, dosis, registros, suministros y reportes.
- `/api/v1/medications/overview`, `/patients`, `/alerts`, `/inventory`: consultas ERP filtradas por ámbito.
- Operaciones ERP por paciente/recurso para registro, revisión y seguimiento.
- Recursos privados de foto/documento con autorización y enlaces de vida limitada.

Reutilizar los guards y la resolución de permisos existentes. El guard actual de paciente no sustituye por sí solo el filtro de clínica: aplicar ambos. No usar directamente el envío genérico de notificaciones como una acción de medicamentos autorizada.

Mantener una proyección compatible en la ficha actual del paciente. Migrar las filas antiguas conservando procedencia; una frecuencia libre no se convierte automáticamente en una pauta ejecutable. Índices únicos de ocurrencia y claves de operación, transacciones y control de versión protegen contra duplicados y actualizaciones concurrentes.

## Reglas de datos y recordatorios

- Futuro/pendiente, tomado, tomado fuera de ventana, omitido por el paciente y sin registro son estados distintos. La falta de respuesta no demuestra una omisión.
- Los ±60 minutos, tres recordatorios y umbrales 70/85 del demo son ejemplos, no reglas aprobadas para todo medicamento. Definir la política de horarios y métricas antes de activar automatizaciones.
- El consumo depende de cantidad y unidad, no del número de horarios. Existencia desconocida, pautas a demanda o unidades no convertibles producen una estimación no disponible. Una discrepancia de suministro no bloquea el registro de una toma.
- Confirmar dos veces o reintentar una petición no duplica la toma ni descuenta existencias dos veces. Una corrección genera los movimientos compensatorios correspondientes.
- Pausar/finalizar una pauta cancela ocurrencias y avisos futuros de forma trazable. Cambiarla no reinterpreta el pasado.
- Programar con zona horaria y probar cruces de medianoche y cambio de hora. Confirmar o reportar omisión cancela recordatorios pendientes de esa ocurrencia.
- Reutilizar transporte FCM y bandeja de notificaciones, con deduplicación y política propia para medicamentos. La lógica de logros o el límite genérico de un envío diario no determina los recordatorios de dosis.
- Comprobar estado, acceso, consentimiento y preferencias antes de cada envío. Distinguir programado, enviado, aceptado por proveedor, entregado y confirmado por el paciente.
- WhatsApp puede incorporarse después con consentimiento/preferencias y plantilla apropiada aprobada; no reutilizar plantillas SOS.
- La primera entrega puede registrar online, mostrando error/reintento sin fingir confirmación si falla la red. Diseñar soporte offline protegido y por cuenta antes de activarlo; no introducir estos datos en la cola genérica de localStorage de 72 horas.

## Interacciones y contenido clínico

La fuente de interacciones está pendiente. Hasta disponer de un proveedor verificable, cobertura de identificadores y atribución/versionado, mostrar **no evaluado/no disponible**, no “sin interacciones”. No usar un LLM como autoridad de interacciones ni copiar las severidades del demo como reglas reales.

Mantener procedencia de las pautas y materiales publicados. Los valores clínicos y ventanas de seguimiento quedan por especificar con el responsable del producto; el código no debe inventarlos.

## Orden de implementación y aceptación

| Entrega | Trabajo | Evidencia para terminar |
| --- | --- | --- |
| 1. Accesos y contratos | Capacidades compartidas, habilitación por usuario, permisos, ámbito y contratos de medicamentos | Pruebas de acceso permitido/denegado, otro paciente/otra clínica, enlaces directos y retirada de acceso |
| 2. Circuito app y ERP | Módulo Nutracéutico en lugar de Mi perfil, Medicamentos dentro de él, alta manual de tratamiento/pauta, Hoy, registro de toma/omisión, historial ERP y resumen en Perfil 360 | Paciente registra tratamiento y toma; el ERP autorizado ve el mismo dato persistido; reintento no duplica |
| 3. Seguimiento operativo | Suministros, síntomas, alertas, recordatorios FCM, auditoría de lectura/exportación e informes | Inventario consistente, alertas trazables, cancelación/deduplicación de avisos, exportación limitada al ámbito |
| 4. Contenido por usuario | Extensión de capacidades y asignaciones a nutrición, ejercicios, podcasts y contexto de chat, empezando sus controles de entrada desde la entrega 1 | Mismo usuario recibe solo lo publicado/asignado; cambios de disponibilidad afectan app, ERP, enlaces, APIs y caché |
| 5. Foto y fuentes | Captura asistida con confirmación, fuente de interacciones cuando esté definida, canal WhatsApp si se incorpora | Fallos de extracción no activan pautas; cobertura desconocida se declara; transportes y acceso comprobados |

Las entregas 4 y 5 son independientes: una fuente de interacciones pendiente no retrasa los controles de contenido por usuario. La entrega 2 es la primera demostración funcional completa. Las pantallas con datos de ejemplo no sustituyen este circuito ni se presentan como funcionalidades listas para producción.

## Verificación antes de implementar y publicar

Aplicar test-first: escribir primero recorridos Playwright del comportamiento esperado, comprobar que fallan por ausencia de funcionalidad, implementar y obtener el resultado correcto. En esta tarea de planificación no se ejecutaron pruebas de funcionalidades nuevas ni se modificó código de producto.

Recorridos mínimos:

- Alta propia desde la app, lectura ERP autorizada y confirmación de dosis con persistencia real en BD aislada.
- Acceso propio frente a paciente ajeno y aislamiento entre clínicas; incluir búsquedas, totales, archivos y exportaciones.
- Auditor y personal de solo lectura sin posibilidad de modificar ni exportar sin permiso específico.
- Retirada de módulo/permiso, cierre de sesión y cambio de cuenta sin datos, caché o trabajos pendientes de otro usuario.
- Reintentos, doble pulsación y concurrencia de inventario con una sola aplicación del efecto.
- Cambios de pauta, pausas, correcciones, medianoche y cambio de hora sin alterar el historial.
- Recordatorios cancelados al registrar una toma, estados fallidos/desconocidos y retirada de consentimiento.
- Solo contenido publicado/asignado en app y chat; notas internas y borradores ausentes de las respuestas.
- Nutracéutico reemplaza Mi perfil en Inicio y Perfil en la barra inferior; Medicamentos se abre dentro de Nutracéutico y la cuenta sigue accesible desde el avatar. La lección del programa no se contabiliza como toma.
- ES/EN, claro/oscuro, móvil/escritorio, teclado, nombres de una línea y estados sin datos.

Usar transportes simulados para los recorridos de CI. La entrega real de notificaciones en segundo plano se comprueba aparte en iOS y Android físicos; un navegador no acredita ese comportamiento.

Decisiones abiertas que no bloquean el circuito manual: proveedor de interacciones, extracción por foto y política de recordatorios/umbrales. La habilitación de cada automatización requiere cerrar su política y fuente, con estados explícitos mientras no esté disponible.

## Puntos del código revisados

- Backend: `Domain/Entities/Medication.cs`, `PatientMedication.cs`, `Application/Features/Patients/UpdatePatientCommand.cs`, `Application/Services/PatientScopeGuard.cs`, `Api/Context/CurrentContext.cs`, `Api/Controllers/CatalogsController.cs`, `Api/Controllers/NotificationsController.cs`, `WhatsApp/Reminders/` y `docs/modules/legal-consents/README.md` (bajo `src/CoppAddresd.*` según proyecto).
- ERP: `providers/context-provider.tsx`, `lib/config/navigation.ts`, `lib/config/feature-visibility.ts`, `features/patients/services/prescriptions-service.ts`, `features/patients/services/catalogs-service.ts`, `features/program/components/program-patient-profile360.tsx` y `features/program/services/program-content-service.ts`.
- App: `src/types.ts`, `src/App.tsx`, `src/context/AppContext.tsx`, `src/utils/authApi.ts`, `src/components/BottomNav.tsx`, `src/services/program/`, `src/pages/program/NutraceuticLesson.tsx`, `src/utils/pushNotifications.ts` y `src/services/offline/types.ts`.
