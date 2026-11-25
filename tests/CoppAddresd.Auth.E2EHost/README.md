# Activación por SMS sin correo

Este host verifica la app con Auth, Identity y PostgreSQL reales. Sustituye solo
el cliente de red de Twilio. No envía SMS, correo ni llamadas reales.

Requiere una base local cuyo nombre empiece por `copp_review_`, con los esquemas
de Auth y API migrados. Los cuatro pacientes de prueba usan los documentos
`QA-OTP-NO-EMAIL`, `QA-OTP-EXPIRED`, `QA-OTP-WRONG`, `QA-OTP-BAD-EMAIL`, el país
`1` y los números ficticios `2025550101` a `2025550104`. Todos carecen de correo
salvo el último, que usa `not-an-email` para comprobar que la validación sigue
activa. La aplicación `app` debe existir y estar activa en Auth.

La configuración se suministra por variables de entorno o archivo privado,
nunca en Git: `ConnectionStrings__DefaultConnection`, `Jwt__Secret`,
`Jwt__Issuer`, `SkipDatabaseInitialization=true`, `COP_E2E_OUTPUT` (bajo `/tmp`)
y las opciones `Twilio__*` con valores exclusivamente sintéticos de prueba.

```sh
dotnet run --project tests/CoppAddresd.Auth.E2EHost
```

El host solo escucha en `127.0.0.1:5125`. Con la app en Vite (`5173`), desde la
raíz del backend:

```sh
COP_E2E_OUTPUT=/tmp/mi-evidencia \
COP_E2E_DATABASE=copp_review_mi_base \
COP_E2E_AUTH_URL=http://127.0.0.1:5125 \
node tests/CoppAddresd.Auth.E2EHost/activation.mjs
```

El script necesita Playwright instalado en el repositorio hermano de la app
(`COP_E2E_APP_DIR` permite otra ubicación) y un contenedor PostgreSQL local
en el contexto Docker `default`. `COP_E2E_POSTGRES_CONTAINER` permite usar otro
contenedor en lugar de `copp-qa-postgres-20261013`. Reinicia únicamente
los usuarios sintéticos vinculados a los cuatro documentos dentro de esa base.
Los códigos `123456`, `000000` y `999999` simulan aprobación, verificación
consumida/vencida y rechazo. Nunca se aceptan en la integración de producción.

## Inicio de sesión por correo (KAN-117)

La prueba `e2e/email-login.mjs` de la app usa el flujo normal del navegador.
Levanta Vite con el proxy al Gateway local. El Gateway debe enrutar a Auth
y a la API, ambos conectados a una base PostgreSQL desechable con sus
migraciones aplicadas. Configura el mismo emisor y la misma clave JWT
sintéticos para ambos servicios.

Siembra un paciente con `DevPatient` en Auth después de migrar ambos
esquemas. Usa su correo, contraseña e IDs de Auth y perfil vinculado como
entrada. Ejecuta desde el repositorio de la app:

```sh
COP_E2E_APP_URL=http://127.0.0.1:5173 \
COP_E2E_PATIENT_EMAIL="$qa_patient_email" \
COP_E2E_PATIENT_PASSWORD="$qa_patient_password" \
COP_E2E_USER_ID="$qa_auth_user_id" \
COP_E2E_PROFILE_ID="$qa_patient_profile_id" \
node e2e/email-login.mjs
```

Comprueba credenciales inválidas, aceptación del correo con mayúsculas,
identidad y perfil reales, restauración con la cookie HTTP-only y cierre de
sesión. La prueba ejecuta las solicitudes contra los servicios locales.

## Recuperación mediante enlace (KAN-117)

Usa el mismo Gateway, la misma base desechable y un paciente sintético con
correo confirmado. Agrega la web pública de Copp Adresd sobre HTTPS local,
con su proxy de Vite al Gateway, y un buzón SMTP local de Mailpit.
Configura Auth con `Email__PasswordResetUrl` apuntando a la ruta HTTPS
`/recuperar-cuenta`, y con `Email__Provider=Smtp` y host y puerto del buzón
de captura. Para la prueba con dos réplicas, configura el mismo
`DataProtection__KeyRingPath` en ambas y enruta la confirmación en el Gateway
a la segunda réplica.

Desde la app ejecuta `node e2e/email-recovery.mjs` con las variables de
paciente de la prueba de inicio de sesión y además:

- `COP_E2E_RESET_URL` con la URL HTTPS local de la página pública.
- `COP_E2E_INBOX_URL` con la URL HTTP local de la API de Mailpit.
- `COP_E2E_NEW_PASSWORD` opcional con la contraseña nueva del paciente sintético.
- `COP_E2E_BROWSER=webkit` opcional para probar con WebKit.

La prueba actualiza la contraseña del paciente. Usa su contraseña vigente
y elige una nueva distinta en cada repetición.

Para probar el vencimiento, ejecuta el host E2E en `:5125` con
`COP_E2E_EXPIRE_RESET_LINKS=true` y el mismo anillo de claves. Enruta solo la
confirmación a ese host. Ejecuta la prueba con la misma variable para
verificar que el enlace vencido se rechaza y la sesión previa sigue activa.
