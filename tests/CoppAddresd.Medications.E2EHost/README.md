# Host real de Control de medicamentos

Infraestructura local de prueba; no despliega producto ni envía mensajes. Ejecuta Auth e Identity reales en 5163, la API real en 5162 y un relay HTTP de transporte en 5160. PostgreSQL está aislado en 55437 y solo admite bases cuyo nombre empiece por `copp_review_medications_`.

El host genera claves y contraseñas efímeras, migra Auth y AppDbContext, y crea seis cuentas sintéticas (incluye permisos limitados a una clínica) y dos pacientes en clínicas distintas, con un programa mínimo para el primero. Guarda credenciales solo en `/tmp/copp-medication-e2e/fixture.private.json` con permisos privados. No imprimir ni versionar ese archivo. No carga archivos de configuración privada del desarrollador.

## Inicio

Desde el backend, con Docker disponible:

```sh
docker run --detach --rm --name copp-medications-e2e \
  --publish 127.0.0.1:55437:5432 \
  --env POSTGRES_USER=app_user \
  --env POSTGRES_DB=copp_review_medications_20261117 \
  --env POSTGRES_HOST_AUTH_METHOD=trust \
  --tmpfs /var/lib/postgresql:rw pgvector/pgvector:pg18

dotnet run --project tests/CoppAddresd.Medications.E2EHost \
  -p:AllowMissingPrunePackageData=true
```

La autenticación sin contraseña de PostgreSQL se limita a este contenedor efímero y su puerto loopback; no es configuración de producto. No reutilizar una base de desarrollo ni producción.

En el ERP:

```sh
NEXT_PUBLIC_GATEWAY_URL=http://127.0.0.1:5160 \
NEXT_PUBLIC_APPLICATION_CODE=erp NEXT_PUBLIC_APPOINTMENTS_ENABLED=false \
node node_modules/next/dist/bin/next dev --webpack --hostname 127.0.0.1 --port 3100
```

En la app:

```sh
VITE_GATEWAY_BASE_URL=http://127.0.0.1:5160 \
VITE_PROGRAM_API_ENABLED=true VITE_APPOINTMENTS_ENABLED=false \
npm run dev -- --host 127.0.0.1 --port 5188 --strictPort
```

Next permite un servidor de desarrollo por repositorio: cerrar otro servidor del mismo checkout antes de usar 3100. No cambiar el gateway privado del desarrollador ni el gateway publicado.

Desde el ERP:

```sh
node node_modules/@playwright/test/cli.js test \
  --config playwright.medications-real.config.ts
```

## Evidencia y límites

El endpoint local de infraestructura `POST /__qa/clock` controla únicamente el `TimeProvider` inyectado en la API de QA. Permite comprobar pausas que cruzan medianoche sin modificar filas ni simular respuestas. Cada escenario restablece el reloj al terminar. No existe en el producto.

La suite usa APIs y filas reales, sin `page.route` para datos de negocio. Incluye alta/toma desde la app, lectura ERP, reintentos concurrentes, denegación por paciente/profesional/permisos, retirada de módulo, y guardados del perfil móvil y agregado ERP después de registrar tomas. Se verifican también filas directamente mediante `docker exec ... psql` en la base aislada.

El contrato de perfil del build 67 se referencia desde el commit `c542af2e0aa60644b833496debfbe8918f93d603`. Esto verifica compatibilidad del contrato y persistencia; no ejecuta el IPA ni acredita comportamiento nativo en un iPhone. Transportes externos, Bluetooth, entrega push y App Review quedan fuera de este host.

La suite registra screenshots explícitos de pantallas autenticadas con pacientes sintéticos; no captura el login. Las trazas, videos y screenshots automáticos están desactivados para evitar capturar credenciales. Los reportes contienen resultados y referencias de código, no tokens.

Para detener: Ctrl+C en los servidores y `docker stop copp-medications-e2e`. El contenedor `--rm` y su almacenamiento temporal se eliminan; solo son datos sintéticos de QA.

## Reportes de esta ejecución

Evidencia local, fuera de Git:

- `/tmp/copp-medication-e2e/red-capabilities.log`: perfil anterior200 y nueva capacidad404 antes de implementar.
- `/tmp/copp-medication-e2e/red-regressions.log`: fallos reales de permiso scoped, escritura por GET y reutilización de clave no-op.
- `/tmp/copp-medication-e2e/red-lifecycle.log`: una dosis de la pausa nocturna aparecía pendiente.
- `/tmp/copp-medication-e2e/green-final.log`: ejecución completa posterior a las correcciones.
- `/tmp/copp-medication-e2e/playwright/`: contrato congelado del perfil67, reporte JSON sin tokens y screenshots app/ERP con pacientes sintéticos.

La suite comprueba siete recorridos: preflight de contrato; habilitación ERP/alta y toma app/consulta ERP; persistencia e idempotencia con guardados de perfil y retirada de acceso; permisos scoped; GET sin escrituras; reserva de operaciones aceptadas sin cambio; y pausa nocturna/reanudación. El reloj de QA siempre se restablece al terminar. Los procesos permanecen limitados a loopback; estos puertos no indican despliegue ni habilitación del módulo publicado.

## Acceso habilitado por defecto

`medications-default-access-real.spec.ts` en ERP usa esta misma infraestructura para comprobar acceso sin override, estado vacío en la app, creación/toma sin activación, restricción/restauración desde ERP, permisos de solo lectura, consistencia del programa y exclusión de pacientes inactivos. Usa una base sintética dedicada (por ejemplo `COP_MEDICATION_DATABASE=copp_review_medications_defaults_20261118`) y nunca datos del desarrollador o producción. Los resets de excepciones/estado solo afectan al paciente sintético fijo de esa base.
