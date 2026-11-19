# KAN-74 · WhatsApp Fase 2

## 8.1 Matriz de paridad

Auditoría del backend sobre `4f6634a9` y del AI Service Ola A en `4d17bbd`.
El árbol de AI en `origin/dev` (`3c7e1fc`) añade indexación privada y conserva
la exclusión del runtime Vita para `agent=whatsapp`. Los datos de QA son
sintéticos y el transporte HTTP hacia AI y Meta se simula en las pruebas de WhatsApp.
8.1 queda abierto en AI. 8.2 y el contrato backend de 8.3 tienen cobertura local.

El POST autenticado con `X-Internal-Key` a `/api/v1/chat` lleva
`agent=whatsapp`, `supports_vita=true`, `channel=whatsapp`,
`channel_message_id=<wamid>`, `thread_id`, `user_id`, `patient_id` y
`language`. BACK resuelve los UUID desde el remitente del webhook firmado,
revalida el par paciente activo/usuario del vínculo y aplica el consentimiento
de chat. La app deriva su usuario del JWT. Ninguna de las dos rutas obtiene
identidad de los argumentos de una tool o del texto. El test
`TwoEnglishTurns_UseRealChatHandlerAndHttpContract_WithStableThread_AndDedupe`
incluye un `patient_id` ajeno en el texto y comprueba los UUID enviados.

**Estado de AI.** En `app/api/routes/chat.py`, `_is_vita_request` y
`_history_opt_in` excluyen el perfil WhatsApp. `ChatRequest` de
`app/api/schemas.py` aún ignora `channel` y `channel_message_id`.
`app/agents/registry.py` ofrece dos herramientas al perfil de WhatsApp y
`app/tools/policy.py` restringe las lecturas del expediente a texto/voz.
La evidencia por fila mide el contrato o la operación canónica del backend,
además del estado de habilitación observable en el código de AI.

| Tool o capacidad | App / voz | WhatsApp con AI Ola A | Evidencia |
| --- | --- | --- | --- |
| `calculate` | Disponible | Disponible | Ambos perfiles en `app/agents/registry.py` y canal permitido en `app/tools/policy.py`. |
| `get_current_time` | Disponible | Disponible | Mismo registro y política. |
| `get_my_day` | Vita | Pendiente de runtime AI | `Vita_LecturasRealesCoincidenConSnapshotSinMutarSemanasTestsNiXP`, scope `day`. |
| `get_my_nutrition` | Vita | Pendiente de runtime AI | Mismo test, scope `nutrition`. |
| `get_my_assessments` | Vita | Pendiente de runtime AI | Mismo test, scope `assessments`. |
| `get_my_podcast` | Vita | Pendiente de runtime AI | Mismo test y `Podcast_MinimizaDatosYEntregaNavegacionSinUrl`. |
| `get_my_exercise` | Vita | Pendiente de runtime AI | Mismo test y `Vita_DescansoRealYIdentidadAjenaSinContenidoInventado`. |
| `get_my_progress` | Vita | Pendiente de runtime AI | `Vita_WearableRealSinPrograma_AislaOrigenPacientePeriodoYNoSumaSnapshots`. |
| `get_my_profile` | Vita | Pendiente de runtime y permiso AI | `VitaA_Profile_IncluyeMetasAlergiasYAntropometriaAisladas`. |
| `get_my_vitals` | Vita | Pendiente de runtime y permiso AI | `VitaA_Vitals_RespetaDiaLocalUnidadOrigenYCorreccionesConfirmadas`. |
| `get_my_medications` | Vita | Pendiente de runtime y permiso AI | `VitaA_Medications_UsaElModuloYTomasDelDiaSinEscribir`. |
| `get_my_nutrition_plan` | Vita | Pendiente de runtime y permiso AI | `VitaRead2_Nutrition_ExposesThirtyPublishedDaysRecommendationsAndOwnHistory`. |
| `get_my_assessment_results` | Vita | Pendiente de runtime y permiso AI | `VitaRead2_Assessments_ReturnsAuthorizedSnapshotAndHidesPendingScores`. |
| `list_my_documents` | Vita | Pendiente de runtime y permiso AI | `VitaRead2_Documents_ListsOwnMetadataAndIssuesShortSignatureOnlyToOwner`. |
| `read_my_document` | Vita | Pendiente de runtime y permiso AI | Mismo test para lectura individual y aislamiento. |
| `retrieve_knowledge` · programa | Vita con bases autorizadas | Pendiente de runtime AI | Scope en `app/vita/runtime.py` y prueba de referencia `test_retrieval_is_prepared_once_from_authorized_scope` en AI. |
| `retrieve_knowledge` · expediente | Vita con scope privado | Pendiente de runtime y permiso AI | `app/tools/retrieval.py` excluye WhatsApp. `VitaDocumentIndexPostgresTests` cubre indexación y `test_runtime_privado_responde_con_fuentes_sin_inyectar_programa` es la prueba de referencia en AI. |
| `record_measurement` · peso y signos | API de captura compartida | API acepta `vita_whatsapp`; ejecución del turno pendiente de AI | `VitaCapture_Measurement_PersistsOneReceiptUnderConcurrentReplay_AndReadsIt`; procedencia WhatsApp visible desde la app y el ERP en `VitaProvenance_AppAndErpExposeRecordedChannelReporterAndStatus`. |
| `record_medication_dose` | API de captura compartida | API acepta `vita_whatsapp`; ejecución del turno pendiente de AI | `VitaCapture_Medications_ReusesCanonicalDosesAndRollsBackOnReceiptFailure` comprueba ese canal en el recibo. |
| `record_meal` | API de captura compartida | API acepta `vita_whatsapp`; ejecución del turno pendiente de AI | `VitaRead2_ProgramCapture_CancelsAndReplaysWithoutDuplicatingRowsOrEvents` verifica origen `vita_whatsapp`. |
| `record_checkin` | API de captura compartida | API acepta `vita_whatsapp`; ejecución del turno pendiente de AI | Mismo test para check-in y replay en chat y WhatsApp. Un cuestionario semanal distinto del check-in diario requiere su propio flujo de ejecución. |
| `get_patient_data` | Perfil legado autorizado | Excluida por política AI | `app/tools/policy.py`. Las lecturas específicas de Vita cubren el expediente por alcance. |
| `mark_control_declined` | Solo chat de texto de la app | Excluida por política AI | `app/tools/policy.py`. Corresponde al rechazo de subir un examen del control de programa. |
| `suggest_appointment` | Retirada para pacientes | Retirada para pacientes | `DISABLED_PATIENT_TOOLS` en `app/tools/registry.py`. |

La API canónica de captura exige propuesta, consentimiento vigente y confirmación
explícita. El runtime debe conservar `vita_whatsapp` y el identificador del turno
de origen al confirmar. El `wamid` completo identifica el mensaje de proveedor.
El `turnId` de captura admite hasta 256 caracteres y puede derivarse de forma
determinista si el ID original supera ese límite. La aplicación de esa
procedencia y del `wamid` en AI queda pendiente de 8.1. Etiquetar el turno como texto para forzar el runtime de
la app produciría procedencia incorrecta.

## 8.2 Formato y entrega

Se aplican encabezados legibles, énfasis de WhatsApp, enlaces con su destino y
tablas en texto. Los cortes prefieren párrafos, líneas y espacios. Cada parte
se limita a 4096 unidades UTF-16 y la concatenación reconstruye exactamente el
texto formateado. Un grafema individual mayor al límite se corta entre escalares
Unicode. Los tests prueban 5000 caracteres, fronteras 4095/4096/4097, emojis
compuestos, banderas, tildes combinadas y reanudación tras error transitorio.

`whatsapp.raw_events` persiste `reply_fragments` y el cursor de entrega. Un
evento legado con cursor avanzado mantiene su secuencia anterior. La migración
`PersistWhatsAppReplyFragments` agrega dos columnas sin reescribir datos y su
reversión elimina esas columnas. Los workers de la versión anterior deben
drenarse al cambiar de versión. Si Meta acepta un fragmento y la confirmación
se pierde antes de guardar el cursor, sigue existiendo una entrega ambigua.

## 8.3 Contrato de sincronización

- El hilo predeterminado es `proactive-{userId:D}`. `userId` corresponde al
  UUID de `auth.users`. AI lo usa junto con `thread_id` para aislar el historial.
- Tras validar el vínculo, WhatsApp escribe y usa esa clave. Para un registro
  anterior, el primer turno válido cambia el puntero local al hilo de la app.
  El historial de los hilos `whatsapp-*` previos requiere una importación
  específica en AI. Los turnos nuevos se envían con la clave compartida.
- Chat síncrono, streaming y sesión de voz Vita eligen esa clave si el cliente
  omite el hilo. Los IDs explícitos se conservan por compatibilidad. En la app
  actual, la pantalla de chat y la apertura de voz ya comparten el ID.
- El webhook autentica y persiste el evento y la interacción en una sentencia
  antes del ACK. La prueba `BlockedReminderUpdate_ReturnsRetryableResponseAndPreservesTheInteraction`
  verifica el 429 en menos de cinco segundos y confirma que la reentrega aplica
  la interacción completa. Un `wamid` repetido incrementa el contador y deja
  intacto el turno. El lease y `agent_started` previenen
  ejecuciones dobles al recuperar un worker. Un intento cuyo resultado se
  desconoce se resuelve con un mensaje seguro. Si AI devuelve un hilo distinto,
  se conserva el hilo canónico y se registra `agent_thread_mismatch`.
- La app consulta `GET /api/v1/threads/{threadId}/messages` con la identidad
  del JWT. La prueba `WhatsAppAndApp_ReadAndContinueTheSameOwnedThread`
  usa el cliente AI y los controladores de app reales con un servidor de AI
  simulado. Comprueba el reporte visible, una confirmación síncrona o por SSE
  con su contexto, la continuación desde WhatsApp con el contexto de la app y
  una sola ejecución por `wamid`. El runtime desplegado aún debe verificar la
  persistencia del ID de proveedor, la captura cruzada y la continuidad de los
  seguimientos en los tres canales.

## 8.4 Consentimiento y Meta

- **Baja y reactivación verificadas localmente.**
  `Commands_UpdatePreference_WithoutAgent` cubre SALIR/STOP e INICIAR/START,
  incluyendo mayúsculas y acentos. `PermanentError_Cancels_AndOptOutNeverSends`
  verifica que el worker de plantillas no vuelve a llamar al transporte tras
  una baja. El webhook cancela recordatorios pendientes por interacción antes
  del ACK. La preferencia se actualiza en el worker entrante.
- **Bloqueo sin opt-in pendiente en backend.** La vinculación por teléfono y por
  OTP inserta `opt_out=false`. El scheduler y el procesador filtran solo por
  opt-out. `Retry_FailsTwiceThenSucceeds_WithThreeDurableDeliveries` llega al
  transporte simulado desde un vínculo recién creado sin comando de alta.
  La verificación de 8.4 deja abierto el consentimiento explícito y su
  trazabilidad antes de habilitar plantillas.
- **Pendiente externo Meta.** El estado de workspace registra un P0 por ausencia
  de eventos entrantes en el número de Vita. Sigue pendiente verificar con
  Meta la recepción de un mensaje y el estado de entrega de una plantilla
  aprobada fuera de ventana usando un destinatario de QA autorizado.
  La ejecución de KAN-74 usa transporte simulado y cero envíos a pacientes.

## Verificación local

Se usó PostgreSQL 16 en un contenedor efímero dedicado. Las suites de
WhatsApp y Vita ejecutaron las migraciones e integraciones contra esa base.

| Comando | Resultado |
| --- | --- |
| `dotnet build CoppAddresd.slnx` | 0 errores. 2 advertencias de xUnit en `BindingFlowTests.cs`. |
| `dotnet test tests/CoppAddresd.WhatsApp.Tests --no-build` | 142 aprobados, 0 omitidos. |
| `dotnet test tests/CoppAddresd.IntegrationTests --no-build --filter FullyQualifiedName~Vita` | 31 aprobados, 0 omitidos. |
| `dotnet test tests/CoppAddresd.UnitTests --no-build` | 3.104 aprobados, 105 casos que requieren PostgreSQL omitidos en esta corrida. |

El filtro de `VoiceSessionCommandHandlerTests`, `ChatCommandHandlerTests` y
`AiServiceClientTests` ejecutó 38 casos sin fallos ni omisiones.
