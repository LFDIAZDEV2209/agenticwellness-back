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
   dispositivo del paciente. La bandeja funciona sin credenciales de push.
4. Ajusta `Medications__ReminderFollowUpMinutes` si hace falta. El valor
   predeterminado es 30 minutos y el rango permitido es 15–90.

Se mantiene deshabilitado por defecto en ambientes existentes. Los mensajes
son discretos en todos los dispositivos. La carga de push solo contiene el
tipo, destino y un identificador opaco de notificación.

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
