# Membresía web de demostración

[KAN-116](https://diomedescerda.atlassian.net/browse/KAN-116) cubre el inicio
del recorrido de bienestar después del pago. El checkout conectado funciona
solo en Development, con activación explícita, orígenes de loopback y SMTP
local. La interfaz muestra el modo demo y no recoge medios de pago. El modo
local del sitio sin backend sigue siendo una simulación independiente.

## Ciclo y acceso

| Acción de prueba | Efecto en Auth |
| --- | --- |
| Iniciar prueba | Registra una referencia con siete días sintéticos, sin cobro, correo ni acceso premium. |
| Confirmar pago | Activa un período mensual o anual, registra el importe del plan y encola un solo correo de bienvenida. |
| Renovar | Extiende un período desde el fin vigente o desde ahora si ya venció. No crea otra cuenta ni otro flujo de onboarding. |
| Vencer | Cierra el período con efecto inmediato. La API también compara la fecha UTC en cada petición aunque no haya llegado un evento. |
| Cancelar | Cierra el período con efecto inmediato en la simulación. Otro evento no reactiva la misma referencia cancelada. |

El correo lleva el identificador de activación original. El alta o la
vinculación existente registra su `LinkedUserId` en la misma referencia. Los
endpoints de generación del flujo de bienestar exigen una membresía vigente,
la identidad App y los consentimientos actuales. La pérdida de vigencia no
borra respuestas ni planes. Una renovación devuelve el acceso al mismo plan.
Este control no redefine permisos de otras funciones de la app.

## Idempotencia y conciliación sintética

- El checkout conserva el `paymentId` y la clave de esa compra en su sesión.
  Auth guarda solo el hash de la clave. Un comprobante leído por referencia
  no entrega la clave ni permite cambiar el estado.
- La confirmación inicial conserva el primer registro y la cola de correo en
  una sola escritura. Un evento posterior lleva `paymentId`, `eventId`,
  `expectedRevision` y el tipo. Auth bloquea la fila de esa compra, aplica
  el cambio y registra el evento en la misma transacción.
- Repetir exactamente un evento devuelve la vista actual sin extender ni
  cobrar dos veces. Una revisión vieja o un evento reutilizado para otra
  operación devuelve 409. Una clave ajena no da acceso.
- `auth."PaidActivations"` guarda el recibo inicial, el estado y el enlace a
  la cuenta. `auth."PaidMembershipDemoEvents"` guarda eventos por referencia
  y revisión. Para conciliar una compra sintética se suma el recibo inicial
  si hubo confirmación directa, más los eventos `activate` o `renew`. En
  una prueba que pasa a paga, el primer importe está registrado como
  `activate` y no se suma otra vez como recibo inicial. Nunca son cargos reales.

## Verificación y pendientes de lanzamiento

Las pruebas Playwright de checkout viven en el repositorio web.
`e2e/paid-membership-lifecycle.mjs` verifica ciclo, recarga, replay, conflicto,
importe y clave de compra. `e2e/paid-customer-journey.mjs` usa PostgreSQL,
Mailpit, Auth, API, Gateway y los dos frontends locales para verificar alta,
vinculación, onboarding, generación y acceso tras vencer, renovar o cancelar.
El script [seed de QA](../../../scripts/qa/seed-paid-customer-journey.sql)
solo admite una base desechable `copp_review_*`.

El proveedor en vivo y la política de renovación, cancelación y reembolsos
aún requieren decisión de negocio. Sus webhooks autenticados, estados de
pago y conciliación contra liquidaciones reales requieren ese contrato y su
sandbox. Los textos de prueba y la duración de siete días no son una
política aprobada para producción.
