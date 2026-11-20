# Recordatorios de tomas (KAN-78)

El worker se ejecuta cada 30 segundos. Proyecta las mismas ocurrencias que la
agenda paciente y usa la zona IANA de la pauta vigente. El aviso inicial se
puede entregar durante los diez minutos siguientes a la hora programada, y el
seguimiento solo si la toma sigue pendiente. No usa el aviso de existencias ni
los alertas de efectos como disparadores.

## Activación

1. Aplica la migración de API `AddMedicationReminders`.
2. Habilita `Medications__Enabled` y `Medications__RemindersEnabled`.
3. Para entrega nativa, configura el transporte FCM existente y registra el
   dispositivo del paciente. Android requiere `google-services.json`. iOS
   necesita la configuración Firebase del mismo proyecto y un perfil con
   Push Notifications. El cliente iOS intercambia el token APNs por un token
   FCM compatible con el backend. La bandeja funciona sin credenciales de push.
4. Ajusta `Medications__ReminderFollowUpMinutes` si hace falta. El valor
   predeterminado es 30 minutos y el rango permitido es 15–90.

Se mantiene deshabilitado por defecto en ambientes existentes. Los mensajes
son discretos en todos los dispositivos. La carga de push solo contiene el
tipo, destino y un identificador opaco de notificación.

## Prueba en el entorno compartido

La variable de repositorio `MEDICATION_DEMO_REMINDERS_ONLY_DEV=true` activa
push y bandeja del motor únicamente para el paciente sintético reservado
`900006702` al desplegar la API. El worker verifica tanto la identidad del
paciente como la cuenta asociada, y «Posponer» se limita a ese paciente.
Mientras está activa esta opción, el barrido de todos los canales de
medicamentos se limita al caso de prueba. Configura la variable en `false`
y vuelve a desplegar al acabar la prueba.

## Entrega y posponer

La base reserva una fila única por paciente, toma, canal y fase bajo el mismo
bloqueo que actualiza la pauta o confirma la toma. Para la app guarda la fila de
bandeja en la misma transacción. Otra instancia puede ejecutar el barrido
sin duplicar avisos. Antes de invocar el canal se comprueba nuevamente que la
toma siga pendiente y el tratamiento activo. Un cambio de horario elimina la
ocurrencia anterior de los siguientes barridos.

FCM se intenta una vez por token activo después de reservar la entrega. En caso
de caída entre la reserva y FCM, o respuesta ambigua del proveedor, no se
reintenta el mismo aviso externo para evitar duplicarlo. La bandeja permanece
disponible y el siguiente seguimiento puede entregarse si corresponde.

La aplicación autenticada puede llamar
`POST /api/v1/me/medications/doses/{id}/snooze` con `scheduledAt` ISO-8601 y
`minutes` igual a 15 o 30. Devuelve 204 al aceptar, 409 si dejó de ser elegible
y 404 si el canal está desactivado. El posponer sustituye la siguiente fase de
ese canal. Repetir mientras el posponer está pendiente no cambia su hora.

## Validación operativa

En un entorno de prueba, programa una toma cercana en la zona del paciente.
Comprueba un aviso en la bandeja y un push discreto por dispositivo. Toca uno
para abrir Mis medicamentos. Verifica el segundo aviso pendiente, el tiempo
elegido al posponer y la ausencia de seguimientos tras registrar la toma o
pausar el tratamiento. Usa dispositivos iOS y Android del entorno de prueba.

El host de QA `copp_review_medications_kan78_*` permite avanzar el reloj y
ejecutar el worker real desde `POST /__qa/medications/push-sweep`. Usa un
transporte FCM en memoria. La prueba Playwright comprueba la bandeja real y
la carga de push sin enviar avisos a Firebase ni a dispositivos.

## Preferencias y WhatsApp

El paciente puede ajustar push, bandeja, anticipación y segundo aviso. Las
preferencias por pauta pueden restringir un canal o precisar sus tiempos. Un
canal desactivado por el paciente queda desactivado para todas las pautas.
La agenda refleja un posponer de 15/30 minutos tras recargar la app.

Para WhatsApp se requiere vinculación verificada, consentimiento explícito,
ausencia de STOP/SALIR y una plantilla utility aprobada por Meta. Configurar
`Medications__WhatsAppRemindersEnabled=true` y
`Medications__WhatsAppTemplate` únicamente tras esa aprobación. El adaptador
usa `es` y `en_US` con cuatro variables posicionales (nombre, medicamento y
dosis, hora, indicaciones) y tres botones quick reply en orden confirmar,
posponer 30 minutos y omitir. Los botones solo transmiten el identificador
opaco de la reserva y la acción. Cada respuesta revalida la vinculación y la
toma, y usa una clave estable para tolerar un webhook repetido.

**ES** · Hola {{1}}, tienes una toma programada de {{2}} a las {{3}}.
Indicaciones: {{4}}.
Botones: Ya lo tomé · Recordar en 30 min · Omitir hoy

**EN** · Hi {{1}}, your next scheduled dose is {{2}} at {{3}}.
Instructions: {{4}}.
Buttons: I took it · Remind in 30 min · Skip this dose

El host de QA puede ejecutar el planificador y aplicar una respuesta sintética
sin invocar Meta ni FCM. La transcripción de notas de voz entrantes de WhatsApp
no forma parte de este adaptador de botones.
