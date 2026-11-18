# Plan de Control de medicamentos e integración entre ERP y app

Fecha: 17 de noviembre de 2026. Estado: circuito manual app/ERP implementado localmente y en verificación E2E. Sin publicación ni despliegue. El resto de este documento conserva la hoja de ruta; no todas sus fases están implementadas.

Referencia: `erp-medicamentos.html`, adjunto del usuario. Referencias de la revisión original: backend `852d75c8`, ERP `c0abb8f` y app `bf1f401`. La ubicación del módulo y las implicaciones de base de datos se actualizan con la corrección posterior del usuario.

## Resultado esperado

Un módulo de **seguimiento de medicamentos**, integrado al ERP y a la app existente. El paciente registra lo indicado en su prescripción, sus tomas, existencias y síntomas; el personal autorizado consulta y da seguimiento desde el ERP. Se incluyen medicamentos, vitaminas y suplementos, con tipo explícito.

Conservar el alcance del adjunto: la plataforma registra tratamientos existentes; no genera recetas ni decide dosis. La información introducida por el paciente se identifica como reportada por él. Una foto o una extracción de texto no equivale a una prescripción verificada. No se introduce una aprobación clínica obligatoria para cada registro del paciente; si una organización necesita ese flujo, se configura como una capacidad adicional y se diferencia de la simple captura.

Un mismo producto permite habilitar módulos y entregar contenido distinto a cada usuario. La disponibilidad del módulo, los permisos de acción y las asignaciones de contenido son controles separados.

## Base anterior a esta implementación

| Área | Base reutilizable | Trabajo faltante |
| --- | --- | --- |
| Backend | Pacientes, catálogo de medicamentos con NDC/RxNorm, permisos y contexto clínico, almacenamiento privado, auditoría de escrituras, transporte FCM y recibos de consentimiento | Tratamientos longitudinales, pautas estructuradas, ocurrencias de dosis, registros de toma, inventario del paciente, síntomas, alertas y auditoría explícita de lectura/exportación |
| ERP | Sesión, navegación por permisos, cliente HTTP, directorio de pacientes, Perfil 360 y consultas reales al catálogo | Módulo conectado de medicamentos, vistas de seguimiento y acciones autorizadas |
| App | Identidad de paciente, programa y contenido asignado, notificaciones y estructura de pantallas Ionic | Pantallas de medicamentos, capacidades por usuario, destinos de notificación y sincronización de tomas |

La auditoría general añadida en `852d75c8`/`c0abb8f` ya ofrece consulta de metadatos con `Audit.View`; reutilizar su visor y filtros. No incorpora nuevos eventos de lectura o exportación de medicamentos, que siguen pendientes.

El Recetario antiguo del ERP usa datos en memoria y su ruta está deshabilitada. No se reactiva como si fuera el nuevo módulo. La lista actual `PatientMedication` solo tiene referencia al medicamento, frecuencia libre y orden; al actualizar el paciente se reemplazan sus filas. El historial nuevo necesita identificadores propios estables.

El HTML tiene datos generados, permisos visuales sin control efectivo, exportaciones y avisos simulados, y una lista fija de interacciones sin proveedor definido. Su inventario global agrupa existencias **en poder de los pacientes**; no representa la bodega del ERP. El botón de efectos secundarios de Inicio tampoco tiene una vista `fx` en el mapa de renderizado del demo.

## Acceso y contenido por usuario

Crear una resolución de capacidades en el backend, compartida por ERP y app, con versión de política. Contrato propuesto: `GET /api/v1/me/capabilities`. La respuesta entrega módulos habilitados y acciones permitidas para la aplicación y el contexto actuales. El contrato del circuito manual está cerrado en `docs/modules/medications/contracts.md`; su implementación y límites se describen en `docs/modules/medications/README.md`.

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

Decisión vigente del usuario: **Control de medicamentos** reemplaza únicamente la tarjeta **Mi perfil** de Inicio. La navegación inferior conserva **Inicio · Nutrición · SOS · Chat · Perfil**. El módulo principal abre el seguimiento de medicamentos y, con acceso habilitado, reemplaza la misión obligatoria Nutracéutico en el protocolo diario. Nutracéutico pasa a una sección adicional del mismo workspace, con un único registro por dosis.

El perfil personal conserva sus pantallas, funciones de cuenta y acceso desde la barra inferior y el menú de usuario.

Dentro de **Control de medicamentos**, la vista principal es **Hoy**. Desde ella se accede a **Mis medicamentos**, **Historial**, **Existencias** y **Reportar síntoma**. Integrar estos destinos al patrón de pantallas actual de la app, sin sustituir todo su sistema de navegación.

Los enlaces antiguos `program-nutraceutico` redirigen al workspace en cuentas habilitadas. Las cuentas sin habilitación y el cliente antiguo conservan la lección y sus contratos. Registrar una dosis no genera XP ni una segunda completación de programa.

1. **Mis medicamentos:** registrar por búsqueda/manual desde una prescripción existente; tipo, presentación, cantidad por toma, unidad, vía, indicaciones, horarios y vigencia.
2. **Hoy (inicio de Control de medicamentos):** próximas tomas y acciones claras de registrar toma u omisión. La app muestra el resultado guardado por el servidor.
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

## Implicaciones de base de datos

Cambiar el acceso de Mi perfil por Control de medicamentos es un cambio de navegación y no exige migrar ni borrar cuentas o perfiles. El seguimiento de medicamentos sí requiere nuevas entidades y migraciones. Propuesta: utilizar el PostgreSQL existente, con el dominio clínico bajo `app` y administrado por `AppDbContext`; ERP y app consultan la misma fuente, sin bases independientes para cada interfaz.

### Base existente y riesgo de compatibilidad

- `app.medications` es el catálogo reutilizable, con nombre e identificadores NDC/RxNorm cuando existen. Vitaminas y suplementos pueden no tener esos identificadores; no convertirlos en campos obligatorios para todos los productos.
- `app.patient_medications` relaciona paciente y medicamento con frecuencia libre, orden y fecha de creación. Tiene UUID, pero el guardado actual del paciente no conserva esas filas.
- `UpdatePatientCommand.ReplaceChildren` vacía la colección y `CreatePatientCommand.ApplyChildren` genera UUID nuevos. Además, `PatientRepository.UpdateAsync` elimina explícitamente los medicamentos anteriores y agrega los entrantes. Conectar nuevas tomas a esas filas sin cambiar ese flujo rompería las referencias o borraría el historial.

Antes de activar dosis e historial, introducir una identidad estable de tratamiento y adaptar todas las escrituras del formulario general del paciente para que no la reemplacen. Mantener la ficha anterior como lectura/proyección compatible del tratamiento canónico; evitar dos listas clínicas editables que puedan divergir.

### Datos nuevos por entrega

La tabla siguiente conserva el diseño de todas las fases. El circuito inicial implementa ocho tablas nuevas bajo `app`: tratamientos, pautas, horarios, dosis confirmadas, eventos de toma, historial de estados, operaciones aceptadas y habilitación por paciente. No incluye tablas de existencias, síntomas o recordatorios.

| Entrega | Familia de datos | Propósito |
| --- | --- | --- |
| Circuito inicial | Tratamientos (`app.medication_treatments`) | Identidad estable, paciente, producto del catálogo o descripción reportada, tipo, procedencia, actor, vigencia y estado |
| Circuito inicial | Versiones de pauta y horarios | Cantidad/unidad, vía, indicaciones, zona horaria y horarios estructurados; cada modificación tiene fechas efectivas y conserva la versión anterior |
| Circuito inicial | Ocurrencias de dosis | Instancias concretas de una pauta versionada, con vencimiento y estado; permiten distinguir lo pendiente de una omisión explícita |
| Circuito inicial | Eventos de toma y corrección | Confirmación u omisión reportada, hora del hecho y de recepción, actor, clave de operación y corrección relacionada |
| Seguimiento | Suministros/lotes y movimientos | Existencias propias por paciente, reposiciones, consumos, ajustes y vencimientos; separados de `erp.inventory_*` |
| Seguimiento | Reportes de síntomas, alertas y entregas | Reporte y seguimiento del caso separados de cada intento de notificación; no afirmar causalidad ni entrega confirmada por aceptar el envío |
| Accesos | Habilitación de módulos por aplicación/usuario | Reutilizar identidad y permisos de Auth; persistir defaults/excepciones del módulo y una revisión de política donde haga falta, sin crear roles por paciente |
| Contenido | Asignaciones existentes y vínculos necesarios | Reutilizar publicación, medios y asignaciones de nutrición/ejercicios/programa; añadir vínculos específicos solo cuando el contenido de medicamentos lo requiera |

El catálogo y la ficha del paciente no se duplican. La fotografía se guarda como objeto privado; la base conserva metadatos y una referencia autorizada, no la imagen en Base64. La auditoría reutiliza la infraestructura existente y añade los eventos explícitos de lectura/exportación pendientes.

### Integridad, crecimiento y acceso

- FK e índices para paciente, tratamiento, versión de pauta y ocurrencia. Definir borrado/restricción de cada relación y conservar el comportamiento de baja del paciente sin eliminar historial por una edición ordinaria.
- Restricción única para cada ocurrencia generada y cada operación idempotente dentro de su ámbito. Los eventos permiten correcciones trazables: una restricción no debe impedir corregir una toma ni permitir aplicar dos veces su consumo.
- Registro de toma, cambio de estado y movimiento de existencias dentro de una transacción, con control de versión/concurrencia. Cantidades con precisión decimal y unidad explícita; no inferir conversiones desconocidas.
- Instantes en UTC/`timestamptz` y zona horaria de la pauta guardada por separado. Conservar la fecha efectiva de la toma, la de recepción y la versión de horario aplicada.
- Las ocurrencias, eventos y entregas crecerán más que el catálogo. Generar un horizonte acotado de dosis futuras, paginar por paciente/fecha y definir retención de eventos, archivos e intentos; no materializar años de dosis por adelantado.
- Para adherencia y KPIs repetidos del ERP, evaluar los rollups CQRS existentes. Un resumen es reconstruible desde eventos y dosis, conserva sus filtros de ámbito y no se convierte en una segunda fuente editable.
- Filtrar por propiedad y ámbito profesional/clínica desde el servidor antes de contar, paginar o exportar. Habilitar un menú no concede acceso a filas de otros pacientes. Las retiradas de acceso invalidan caché y trabajos pendientes.

### Migración y publicación

1. Crear tablas/índices y permisos nuevos mediante migraciones aditivas, sin borrar `app.medications` ni `app.patient_medications`. Las modificaciones que pertenezcan a Auth van en su servicio/contexto e historial de migración, no en `AppDbContext`.
2. Copiar o vincular los registros actuales de forma idempotente, conservando el identificador/procedencia del registro de origen. Importar la frecuencia libre como dato histórico pendiente de estructurar; no inventar dosis pasadas, tomas, adherencia ni existencias.
3. Adaptar el guardado general del paciente y centralizar las modificaciones en el tratamiento canónico antes de activar el módulo. Conservar respuestas compatibles para clientes anteriores durante la transición.
4. Probar la migración y su reejecución sobre una base aislada con datos representativos. Revisar SQL, bloqueos, integridad, compatibilidad con la versión anterior y recuperación desde respaldo. No retirar columnas/tablas anteriores en el mismo despliegue.
5. Habilitar primero para usuarios de prueba. Ninguna migración de esta planificación se ejecuta en producción; cualquier retirada posterior del modelo antiguo necesita una entrega separada y una política de conservación definida.

Incluir en los recorridos test-first de app/ERP: importar datos existentes sin fabricar pauta; guardar datos generales del paciente sin cambiar IDs ni perder tomas; confirmar/reintentar/corregir con un solo efecto en existencias; cambiar de pauta sin reinterpretar el pasado; denegar lectura y exportación fuera del ámbito. Verificar estos escenarios con PostgreSQL real aislado, además de las comprobaciones de navegación.

### Compatibilidad obligatoria con el build 67 enviado a Apple

El build 67 que el usuario identifica como enviado a revisión es un cliente que debe seguir funcionando después de cualquier cambio compartido. La planificación no autoriza sustituir ese build, cambiar su selección en App Store Connect ni desplegar el módulo en producción. La corrección de navegación actual es documentación; no se ha implementado código ni aplicado migraciones de medicamentos.

Base identificada en el registro de CI: versión **1.0**, build **67**, commit **`c542af2e0aa60644b833496debfbe8918f93d603`**, [ejecución 43295056878](https://github.com/FontalvoB/antares-paciente/actions/runs/43295056878), job de firma/subida **131453553246**. El log verifica `CFBundleVersion` del IPA como 67 y registra la subida de ese build a TestFlight. La ejecución de GitHub es la número 92: el número de ejecución por sí solo no equivale al número de build de Apple. Esto identifica el cliente subido; no verifica su ejecución en un dispositivo ni consulta la selección/estado actual de revisión de App Store Connect.

El código de ese commit conserva el destino Perfil y consume `GET`/`PUT /api/v1/me/patient-profile`; el PUT envía datos de contacto, aseguradora y contactos de emergencia. Usar esa referencia congelada para la regresión, no el HEAD actual de `dev`.

Condiciones para publicar el nuevo backend:

1. Conservar rutas, campos, tipos y comportamiento de login/refresh, perfil, nutrición, baterías, chat y demás flujos existentes del cliente congelado. Los nuevos endpoints de medicamentos son aditivos. La consulta nueva de capacidades no se vuelve obligatoria para clientes anteriores ni retira sus permisos actuales.
2. Mantener Mi perfil accesible en el cliente anterior. El nuevo acceso Control de medicamentos pertenece a una versión nueva de la app; una política del backend no debe exigir que el build anterior tenga esa pantalla para funcionar.
3. Preparar migraciones aditivas y probarlas contra PostgreSQL aislado con datos anteriores. No renombrar/eliminar columnas o cambiar restricciones de forma que rompan consultas o escrituras del cliente antiguo.
4. Cubrir expresamente `PUT /api/v1/me/patient-profile`: aunque el cliente móvil envía campos de contacto y no una lista de medicamentos, su handler utiliza `PatientRepository.UpdateAsync`, que hoy elimina/reinserta esa colección. Probar primero la creación de tratamiento/tomas con el módulo nuevo y luego guardar el perfil desde el cliente anterior, verificando que no cambian IDs ni se pierde historial. Repetir con la edición general del ERP.
5. Ejecutar el cliente congelado contra el backend candidato con el módulo nuevo deshabilitado y habilitado para otros usuarios. Un flag de navegación no protege frente a cambios de repositorio, restricciones, permisos o filas compartidas.
6. Verificar el build 67 real en TestFlight/dispositivo contra el entorno candidato antes de declarar aprobado el gate. Si no puede dirigirse con seguridad a un entorno aislado, preparar un equivalente desde su commit con la misma lógica de cliente y validar aparte el binario real; documentar expresamente el límite de evidencia.

Estado del gate: base del build 67 identificada. El backend/migraciones están implementados localmente; se prueba el contrato GET/PUT congelado y la conservación de datos en PostgreSQL aislado. Sigue pendiente ejecutar el binario real de TestFlight en un dispositivo contra el entorno candidato. Una prueba sobre el código actual, una compilación nueva o el hecho de no cambiar el binario no acreditan por sí solos compatibilidad futura con sus servicios compartidos. Mientras el build siga en revisión, mantener los cambios nuevos aislados de producción hasta completar estas comprobaciones.

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
| 2. Circuito app y ERP | Control de medicamentos en lugar de Mi perfil, conservando Nutracéutico separado; alta manual de tratamiento/pauta, Hoy, registro de toma/omisión, historial ERP y resumen en Perfil 360 | Paciente registra tratamiento y toma; el ERP autorizado ve el mismo dato persistido; reintento no duplica |
| 3. Seguimiento operativo | Suministros, síntomas, alertas, recordatorios FCM, auditoría de lectura/exportación e informes | Inventario consistente, alertas trazables, cancelación/deduplicación de avisos, exportación limitada al ámbito |
| 4. Contenido por usuario | Extensión de capacidades y asignaciones a nutrición, ejercicios, podcasts y contexto de chat, empezando sus controles de entrada desde la entrega 1 | Mismo usuario recibe solo lo publicado/asignado; cambios de disponibilidad afectan app, ERP, enlaces, APIs y caché |
| 5. Foto y fuentes | Captura asistida con confirmación, fuente de interacciones cuando esté definida, canal WhatsApp si se incorpora | Fallos de extracción no activan pautas; cobertura desconocida se declara; transportes y acceso comprobados |

Las entregas 4 y 5 son independientes: una fuente de interacciones pendiente no retrasa los controles de contenido por usuario. La entrega 2 es la primera demostración funcional completa. Las pantallas con datos de ejemplo no sustituyen este circuito ni se presentan como funcionalidades listas para producción.

## Verificación antes de implementar y publicar

Aplicar test-first: escribir primero recorridos Playwright del comportamiento esperado, comprobar que fallan por ausencia de funcionalidad, implementar y obtener el resultado correcto. La implementación local utiliza pruebas UI escritas en rojo antes del código y una prueba independiente con API/Auth/PostgreSQL reales. La evidencia y sus límites se registran en `docs/modules/medications/README.md`.

Recorridos mínimos:

- Alta propia desde la app, lectura ERP autorizada y confirmación de dosis con persistencia real en BD aislada.
- Acceso propio frente a paciente ajeno y aislamiento entre clínicas; incluir búsquedas, totales, archivos y exportaciones.
- Auditor y personal de solo lectura sin posibilidad de modificar ni exportar sin permiso específico.
- Retirada de módulo/permiso, cierre de sesión y cambio de cuenta sin datos, caché o trabajos pendientes de otro usuario.
- Reintentos, doble pulsación y concurrencia de inventario con una sola aplicación del efecto.
- Cambios de pauta, pausas, correcciones, medianoche y cambio de hora sin alterar el historial.
- Recordatorios cancelados al registrar una toma, estados fallidos/desconocidos y retirada de consentimiento.
- Solo contenido publicado/asignado en app y chat; notas internas y borradores ausentes de las respuestas.
- Control de medicamentos reemplaza Mi perfil en Inicio y Perfil en la barra inferior; abre directamente Hoy y la cuenta sigue accesible desde el avatar. Nutracéutico conserva su lugar/contenido y su lección no se contabiliza como toma.
- El guardado general del paciente conserva los IDs estables del nuevo tratamiento y su historial. La migración de frecuencia libre no fabrica horarios, dosis, existencias ni porcentajes de adherencia.
- ES/EN, claro/oscuro, móvil/escritorio, teclado, nombres de una línea y estados sin datos.

Usar transportes simulados para los recorridos de CI. La entrega real de notificaciones en segundo plano se comprueba aparte en iOS y Android físicos; un navegador no acredita ese comportamiento.

Decisiones abiertas que no bloquean el circuito manual: proveedor de interacciones, extracción por foto y política de recordatorios/umbrales. La habilitación de cada automatización requiere cerrar su política y fuente, con estados explícitos mientras no esté disponible.

## Puntos del código revisados

- Backend: `Domain/Entities/Medication.cs`, `PatientMedication.cs`, `Application/Features/Patients/UpdatePatientCommand.cs`, `Application/Services/PatientScopeGuard.cs`, `Api/Context/CurrentContext.cs`, `Api/Controllers/CatalogsController.cs`, `Api/Controllers/NotificationsController.cs`, `WhatsApp/Reminders/` y `docs/modules/legal-consents/README.md` (bajo `src/CoppAddresd.*` según proyecto).
- ERP: `providers/context-provider.tsx`, `lib/config/navigation.ts`, `lib/config/feature-visibility.ts`, `features/patients/services/prescriptions-service.ts`, `features/patients/services/catalogs-service.ts`, `features/program/components/program-patient-profile360.tsx` y `features/program/services/program-content-service.ts`.
- App: `src/types.ts`, `src/App.tsx`, `src/context/AppContext.tsx`, `src/utils/authApi.ts`, `src/components/BottomNav.tsx`, `src/services/program/`, `src/pages/program/NutraceuticLesson.tsx`, `src/utils/pushNotifications.ts` y `src/services/offline/types.ts`.
