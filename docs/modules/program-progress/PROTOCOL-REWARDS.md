# Recompensas del protocolo diario · KAN-178

La app autenticada carga su progreso desde `GET /api/v1/people/me/protocol-rewards`. El servidor calcula saldo, nivel y racha. El modo demo sin cuenta conserva el recorrido local. Progreso de Motor 365 (adherencia) y racha de recompensas se mantienen separados.

## Contrato y día local

- Solo se aceptan `exercise`, `diet`, `vitals`, `emotional` y `chest`. `POST /steps` recibe `step` y `localDate`, pero la fecha debe coincidir con el día del perfil calculado en el servidor. Una solicitud desfasada no se reasigna al día siguiente. La confirmación manual del paso, incluido ejercicio reducido, tiene el mismo valor que la confirmación desde el módulo.
- El cofre exige los otros cuatro pasos. La clave `(patient_id, local_date, step)` y el bloqueo del perfil protegen contra reintentos concurrentes. `receipt.created` permite actualizar el estado sin volver a anunciar una recompensa repetida.
- La racha cuenta días consecutivos de cinco pasos. En Motor 365, guardar el registro real confirma el cierre y el bonus en la misma transacción. Una cuenta sin Motor 365 puede cerrar desde `POST /close`. Los reintentos no otorgan otro bonus ni arrastran pasos.
- Las cuentas con Motor 365 o que ya iniciaron el protocolo no pueden crear completaciones nuevas desde la API legada. Sus replays históricos siguen disponibles. La API anterior se mantiene para cuentas aún no migradas.

## Configuración e histórico

- Las seis reglas `PROTOCOL_*` aparecen en **Reglas de XP**. Los valores iniciales reproducen KAN-143 (10 XP por paso y 20 por secuencia) y son editables en el ERP. Se preservan los umbrales de siete niveles, ahora de Principiante a Líder.
- La edición conserva la versión anterior y actualiza la bitácora con el editor autenticado en una transacción. El ERP muestra la configuración guardada, pero cada otorgamiento usa la versión efectiva al inicio de su día local. Una edición exactamente a medianoche aplica al próximo día de esa zona.
- La migración es aditiva. El libro mayor legado conserva IDs, importes y fechas. El nuevo saldo parte del mayor saldo legado alcanzado, sin sumar dos veces saldos acumulados de inscripciones distintas. `GET /legacy-history?page=1` permite al titular recuperar su histórico legado paginado.
- No se importan como XP real agregados de demostración que solo existían en el dispositivo.
