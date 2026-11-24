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

- `Email:Transactional:Name`, `LogoUrl`, `AccentColor` y `SupportWhatsApp` definen la marca. El logo usa `Email:WelcomeLogoUrl` como respaldo.
- `Email:Transactional:Templates:{código}:{Es|En}` admite `Subject`, `Preheader`, `Title`, `Button` y `Callout`. Cada texto admite las variables de su propia plantilla.
- SMTP registra código y estado. Omite asunto, cuerpo y enlaces. `PWD-01` también omite el destinatario.

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

La app solicita el enlace en `POST /api/auth/account/password-reset/request`.
Auth responde con el mismo 202 para correos válidos y encola la consulta.
Solo envía `PWD-01` si el correo de Auth está confirmado y la cuenta tiene
acceso activo a la app. La cola en memoria aplica un minuto entre envíos por
correo y réplica.

Configurar `Email:PasswordResetUrl` con la URL HTTPS pública y la ruta
`/recuperar-cuenta`. El identificador, token y lenguaje viajan en el fragmento
de URL, que la página borra al abrirse. La página entrega el token a
`POST /api/auth/account/password-reset/confirm` dentro del cuerpo de la
solicitud. Tras un único uso válido, Auth actualiza la contraseña e invalida
la renovación de las sesiones existentes en la misma transacción. Los
tokens de acceso ya emitidos conservan su vencimiento. El enlace vence en
60 minutos.

Fuera de Development, configurar SMTP y montar la misma ruta absoluta
`DataProtection:KeyRingPath` en todas las réplicas de Auth. El anillo debe
persistir entre despliegues y quedar protegido en reposo. Verificar el acceso
por HTTPS a la página y a las dos rutas de Auth antes de activar el flujo.

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
