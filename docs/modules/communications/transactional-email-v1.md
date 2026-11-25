# Correo transaccional v1

KAN-98 implementa el catálogo de la sección 3 de *Plantillas de comunicación
Copadres v1.0*. Auth Service renderiza el mensaje a través de
`TransactionalEmailTemplate.Build`. El consumidor entrega el resultado a
`IEmailSender.SendAsync`. La ejecución por eventos, el recordatorio de las 48
horas y la pantalla de restablecimiento corresponden a la orquestación.

| Código | Mensaje | Variables |
| --- | --- | --- |
| BIEN-01 | Bienvenida tras el pago | `nombre`, `correo_pago`, `enlace_descarga`, `soporte_whatsapp` |
| ACT-01 | Cuenta activada | `nombre`, `correo_pago`, `enlace_plataforma`, `soporte_whatsapp` |
| ACT-02 | Activación pendiente | `nombre`, `correo_pago`, `enlace_descarga` |
| PWD-01 | Restablecimiento de contraseña | `correo_pago`, `enlace_restablecer`, `vigencia_enlace` |
| NUT-01 | Primer plan nutricional publicado | `nombre`, `resumen_dia`, `fecha`, `duracion`, `enlace_nutricion` |
| NUT-02 | Nueva versión del plan | `nombre`, `enlace_nutricion` |
| INV-01 | Inventario bajo | `medicamento`, `cantidad`, `dias_restantes`, `enlace_inventario` |

El catálogo contiene asunto, preheader, título, acción, recuadro y texto de
cada versión ES/EN. El idioma usa español como respaldo. El formato alternativo
de texto sale del mismo contenido que el HTML. El resumen usa como máximo 200
caracteres visibles. Envía solo el menú para ese campo. Inventario admite el
nombre del producto y cantidades explícitas. El renderer utiliza las variables
del contrato y escapa su valor en HTML.

El consumidor proporciona todas las URLs de acción. Deben ser HTTPS y
corresponder al entorno oficial. Si falta una URL, la composición falla.
Para correos de cuenta, el destinatario es el correo de acceso. Si se omite
`correo_pago`, se usa el destinatario. El nombre, fecha, duración y resumen
tienen textos alternativos si faltan. `vigencia_enlace` toma los 60 minutos
del proveedor de Identity. El número de soporte puede venir de la configuración.

## Configuración

El nombre público por defecto es **Agentic Wellness IA**. Al construir el remitente SMTP y el valor de `{{marca}}`, los valores históricos exactos se muestran como la marca vigente. Los nombres personalizados siguen siendo válidos. La dirección oficial de contacto y las direcciones de transporte conservan sus valores configurados.

- `Email:Transactional:Name`, `LogoUrl`, `AccentColor` y `SupportWhatsApp` definen la marca. El logo usa `Email:WelcomeLogoUrl` como respaldo.
- `Email:Transactional:Templates:{código}:{Es|En}` admite `Subject`, `Preheader`, `Title`, `Button` y `Callout`. Cada texto admite las variables de su propia plantilla.
- SMTP utiliza el proveedor existente y registra código, destinatario y estado. Los eventos de estas plantillas omiten el asunto, el cuerpo y los enlaces en el registro.

Ejemplo con valores ficticios para previsualizar el contrato:

```json
{
  "code": "NUT-01",
  "recipient": "ana@example.invalid",
  "language": "es-CO",
  "variables": {
    "nombre": "Ana",
    "resumen_dia": "Avena y fruta en el desayuno",
    "fecha": "20 de noviembre de 2026",
    "duracion": "4 semanas",
    "enlace_nutricion": "https://preview.example.invalid/nutrition"
  }
}
```

## Enlaces de restablecimiento

El llamador emite el enlace con `PasswordResetEmailLink.IssueAsync`, pasando
la cuenta activa y la URL HTTPS de restablecimiento de confianza. El enlace
contiene el identificador de cuenta y un token opaco URL-safe. La futura
pantalla entrega el token decodificado a `UserManager.ResetPasswordAsync` para
esa misma cuenta y revoca las sesiones existentes usando el flujo de Auth.
Identity cambia el sello de seguridad al aplicar la primera actualización,
lo que invalida el token. Las réplicas de Auth que emitan o consuman enlaces
deben compartir un anillo persistente de claves de Data Protection antes de
habilitar ese flujo.

## Previsualización y pruebas

```sh
dotnet run --project tools/WelcomeEmailPreview -- /tmp/copp-transactional-preview --transactional
COP_PLAYWRIGHT_PROJECT=/ruta/a/antares-paciente node tools/WelcomeEmailPreview/transactional-email.mjs
dotnet test tests/CoppAddresd.UnitTests --filter FullyQualifiedName~TransactionalEmailTests
```

La herramienta genera 14 archivos HTML y sus mensajes MIME con datos
ficticios. La prueba de render comprueba los enlaces, el texto alternativo y
tres tamaños de pantalla. La prueba SMTP entrega siete mensajes a un buzón
local de captura. La revisión de Gmail, Outlook y un dispositivo móvil
requiere buzones QA autorizados y envío por el transporte configurado.
