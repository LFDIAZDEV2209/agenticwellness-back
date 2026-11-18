# Bienvenidas por acceso — KAN-86

El Auth Service envía una bienvenida por cuenta, personalizada con nombre,
roles guardados y aplicaciones **activas y no suspendidas**. Los módulos del ERP
se obtienen del catálogo y del evaluador real de permisos por scope, incluyendo
Grant/Deny; el nombre del rol no concede accesos. La lista resume capacidades
disponibles en los contextos asignados, sin prometer acceso global.

## Flujos cubiertos

| Alta | Momento de encolar / habilitar entrega |
|---|---|
| Plataforma, individual o CSV | Dentro de la transacción de Identity, después de roles, permisos y acceso ERP |
| Vinculación de cuenta existente | Después de guardar la unión de asignaciones y el acceso ERP |
| Empleado/profesional, individual o CSV | Auth crea una invitación diferida; API confirma tras persistir perfil y scopes |
| Paciente, individual o CSV | El perfil clínico no es una cuenta. Se encola tras verificar OTP y guardar el acceso `app` |
| Cuenta OTP posteriormente invitada al ERP | Conserva su bienvenida de app y recibe la invitación necesaria para establecer contraseña |

La API guarda la confirmación en `erp.welcome_email_finalizations`; un worker
reintenta su entrega a Auth si hay errores HTTP o reinicios. Auth registra el
correo en `auth."WelcomeEmailDeliveries"` antes de confirmar el alta y entrega
fuera de la transacción. Una compensación o scopes incompletos no habilitan el
envío. Un nuevo POST de adopción reutiliza la invitación pendiente registrada;
para cambiar el enlace se usa **Reenviar invitación**.

## Contenido y diseño

- Solo app: instrucciones de identificación + OTP; no aparece el ERP.
- Solo ERP: roles, capacidades habilitadas y enlace al ERP; no aparecen descargas.
- Ambos: las dos secciones y sus instrucciones respectivas.
- Sin contraseña e invitación vigente: el mismo mensaje incluye la creación
  de contraseña. No se envía un segundo correo de primer acceso ni una contraseña.
- `UserPreferences.Lang` selecciona español o inglés; el valor inicial es español.
- Logo original del ERP embebido por CID, paleta azul/verde, HTML con tablas y
  estilos inline, alternativa de texto y adaptación a pantallas pequeñas.
- Los enlaces de tiendas están pendientes de publicación por decisión del
  producto: **iOS · Próximamente** / **Android · Próximamente**, sin enlaces ficticios.

Esta bienvenida es distinta de la bienvenida al programa tras evaluaciones (KAN-15).

## Configuración

Todos los valores pertenecen a `Email` y admiten variables `Email__...`.

| Clave | Comportamiento |
|---|---|
| `Provider` | `Smtp` entrega; `Log` registra solo metadatos y deja estado `Logged` |
| `FrontendUrl` | Base oficial del ERP para `/invitaciones?token=...` |
| `ErpLoginUrl` | Opcional; por defecto `FrontendUrl` + `/login` |
| `IosDownloadUrl`, `AndroidDownloadUrl` | URL oficial HTTPS cuando exista; vacío muestra Próximamente |
| `WelcomeEnabled` | `true` por defecto; `false` pausa el worker sin borrar la cola |
| `WelcomeMaxAttempts` | 5 por defecto (acotado a 1–20) |
| `SmtpHost`, `SmtpPort`, `SmtpUseSsl`, `SmtpUsername`, `SmtpPassword`, `From`, `FromName` | Transporte SMTP existente; secretos fuera del repositorio |

Solo se permiten HTTPS o HTTP de loopback local. El ejemplo de configuración
usa `Log`; no representa una entrega real. En producción el pipeline ya
configura SMTP Microsoft 365 y `https://erp.coppadresd.com`.

## Entrega, idempotencia y recuperación

El worker consulta lotes de 20 cada 15 segundos. Una actualización condicional
reclama cada fila con lease de cinco minutos. Reintenta con espera exponencial
(2, 4, 8… hasta 60 minutos); tras agotar intentos queda `Failed`. Un lease
abandonado se recupera al vencer. No se vuelven a crear usuarios.

La clave única `welcome:{userId}` evita bienvenidas repetidas. Un reenvío explícito
o una primera invitación ERP posterior a la bienvenida de app usa
`invitation:{invitationId}`. Los logins OTP repetidos no modifican una invitación
ni habilitan una entrega ERP pendiente de confirmación.

Estados: `Pending`, `Processing`, `Sent`, `Logged`, `Failed`, `Skipped`.
`Pending` sin `ReadyAt` significa **alta esperando confirmación de API**, no fallo SMTP.
`Sent` significa que SMTP aceptó el mensaje; la recepción o spam se verifica en
el buzón. `Logged` no representa un envío.

- `GET /api/auth/users/{userId}/welcome-emails`: estado, intentos y código de error.
  Exige audiencia ERP + `System.AdminSettings` + `Users.View`.
- `POST /api/auth/users/{userId}/welcome-emails/{deliveryId}/retry`: reintenta
  pendientes/fallidos/registrados, sin recrear cuenta ni cambiar credenciales.
  Exige ERP + `System.AdminSettings` + `Users.Update`. No reenvía filas `Sent`
  ni confirma un alta incompleta.
- La confirmación interna `POST /api/auth/internal/users/{userId}/welcome/complete`
  requiere `X-Internal-Key` y la invitación correspondiente. API la ejecuta y
  reintenta automáticamente desde su registro durable.

La base solo conserva el hash del token. En un reintento SMTP de invitación se
genera otro token para la misma invitación y se invalida el anterior. Cuerpos,
contraseñas, enlaces y errores del proveedor con posibles secretos no se guardan
en el outbox ni se imprimen en logs. Tras una caída exactamente entre la
aceptación SMTP y el registro `Sent` puede llegar una copia: SMTP no ofrece una
transacción atómica con PostgreSQL. No se promete entrega exactamente una vez.

## Pruebas automáticas

```sh
dotnet build CoppAddresd.slnx
dotnet test tests/CoppAddresd.UnitTests
dotnet test tests/CoppAddresd.IntegrationTests --filter 'FullyQualifiedName~WelcomeEmailPostgresTests|FullyQualifiedName~ErpAccessIntegrationTests'
```

Las pruebas PostgreSQL requieren `COP_TEST_DB_CONNECTION` y crean/eliminan bases
efímeras propias. Cubren carreras de enqueue/dispatch, migración Up/Down,
invitación → contraseña → login ERP y rollback del alta. Los tests unitarios
cubren las tres combinaciones de acceso, scopes con Deny, inglés, HTML seguro,
placeholders, reintentos, revocación, suspensión y confirmaciones tras reiniciar.

## Verificación manual

1. Generar correos de muestra sin credenciales ni red:
   `dotnet run --project tools/WelcomeEmailPreview -- /tmp/copp-welcome-preview`.
   Abrir `erp.eml`, `app.eml`, `both.eml`, `english.eml` en Apple Mail y revisar
   también `.html`. Los nombres son ficticios y el token de muestra no funciona.
2. En un entorno de prueba con SMTP/capturador, crear una cuenta de plataforma
   con un buzón propio y permisos conocidos. Debe llegar un solo mensaje, sin
   descarga móvil si solo tiene ERP. Comprobar el login con las credenciales
   establecidas por el flujo de alta; el correo no las reproduce.
3. Crear empleado y profesional con roles de clínica, luego importar CSV mixto
   con filas válidas y fallidas. Solo las altas correctas pueden habilitar correo.
   Verificar nombre, rol, módulos, acciones y límites por clínica.
4. Aceptar la invitación desde un navegador privado, establecer contraseña y
   entrar al ERP. El enlace no debe aceptar un segundo uso. Repetir una adopción:
   debe conservar la invitación pendiente y no duplicar mensajes.
5. Crear paciente y verificar OTP en la app. El alta del perfil sola no manda
   bienvenida. Al existir la cuenta `app`, debe llegar el mensaje sin ERP y con
   las dos tiendas en Próximamente. Invitar después esa cuenta al ERP y comprobar
   que recibe el enlace de contraseña y conserva la app.
6. Configurar un fallo SMTP de prueba. La cuenta debe seguir creada; revisar
   `Pending`/`Failed`, restaurar transporte y reintentar la entrega. Comprobar
   que no cambian ID, roles ni contraseña. Repetir con una interrupción API→Auth
   y verificar la recuperación automática de la confirmación.
7. Revisar en Apple Mail/iPhone y Gmail u Outlook: logo CID, texto alternativo
   si se bloquean imágenes, botones, texto plano, responsive, carpeta spam y
   remitente. Validar SPF/DKIM/DMARC y reglas de Microsoft 365 en el entorno real.
   La vista HTML local no certifica entregabilidad ni todos los motores de correo.

## Despliegue

Migraciones aditivas: `AddWelcomeEmailOutbox` (Auth) y
`AddWelcomeEmailFinalizations` (API). No hay backfill ni envío masivo a cuentas
históricas. Desplegar Auth antes de habilitar las nuevas llamadas de API; durante
la transición soporta clientes anteriores sin `deferWelcome`. El worker de API
tolera que Auth no esté disponible y conserva confirmaciones pendientes. Para
pausar correo, configurar `Email__WelcomeEnabled=false`; conservar los registros.
