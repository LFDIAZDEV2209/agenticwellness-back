# Cámaras familiares · KAN-83

En Antares, abre **Perfil → Cámaras familiares**. Un paciente activo puede crear un equipo o unirse con el código que genera el líder. Cada código vence en 12 horas y se consume al incorporar a un miembro. Cada paciente puede pertenecer a un equipo a la vez.

En **Mis cámaras**, añade una cámara de prueba. Aparece como privada y se puede previsualizar. Activa y guarda el acceso al líder, a administradores del ERP o a ambos, según corresponda. El líder abre **Cámaras compartidas** en Antares. El administrador abre **Pacientes → perfil del paciente → Cámaras**. La API exige audiencia ERP, el rol `Admin`, `Patients.View` y el contexto de clínica u organización cuando está seleccionado.

La API vuelve a comprobar el acceso en cada solicitud de imagen. Retirar una cámara o revocar un acceso detiene la vista en la siguiente solicitud. Salir del equipo o cerrarlo revoca los accesos del líder vinculados a esa membresía. El permiso para administradores se gestiona de forma independiente.

## API y almacenamiento

Bajo `/api/v1/family-cameras`, el paciente autenticado usa `GET /`, `POST /team`, `POST /team/invitations`, `POST /team/join`, `DELETE /team/membership`, `POST /demo`, `PATCH /{cameraId}/sharing` y `DELETE /{cameraId}`. Al actualizar los permisos, solo se modifican los destinatarios incluidos en la petición. Los visores consultan `GET /{cameraId}/frame` en Antares y `GET /patients/{patientId}/{cameraId}/frame` en el ERP. El listado ERP se obtiene en `GET /patients/{patientId}`. Las respuestas de imágenes y códigos se sirven sin caché.

Las tablas se crean en el esquema `app`. Los índices por equipo y por propietario cubren los listados, el índice único de membresía por paciente evita altas simultáneas en dos equipos, y el índice único del hash de invitación permite localizar y consumir el código en una sola operación. El código se guarda como hash. Los índices de claves foráneas cubren la revocación al eliminar una membresía y la limpieza por equipo.

## Conectar el primer dispositivo

El adaptador `IFamilyCameraFeed` recibe el ID interno de cámara tras la autorización del servidor. El proveedor incluido genera JPEG sintéticos en movimiento. La próxima integración usa el modelo real de cámara para elegir el conector y definir la vinculación del dispositivo con el ID interno. Las credenciales y la dirección del equipo permanecen del lado del proveedor. Las vistas solicitan el contenido al API autenticado. El piloto de imagen no requiere permisos de cámara del teléfono ni almacena grabaciones.

## Prueba local con datos sintéticos

1. Crea una base PostgreSQL vacía `copp_review_medications_kan83_qa` con el rol de QA `app_user` y ejecuta `tests/CoppAddresd.Medications.E2EHost` con `COP_MEDICATION_DATABASE` igual al nombre de la base, `COP_E2E_OUTPUT` en una carpeta privada de `/tmp` y `COP_MEDICATION_GATEWAY_PORT=5370`. El host usa el puerto 55437 de PostgreSQL por defecto y acepta `COP_E2E_DATABASE_PORT`.
2. Inicia Antares en `http://localhost:5188` con `VITE_GATEWAY_BASE_URL=http://localhost:5370` y el ERP en `http://localhost:3100` con `NEXT_PUBLIC_GATEWAY_URL=http://localhost:5370`.
3. Desde Antares, ejecuta `KAN83_FIXTURE=/tmp/<carpeta>/fixture.private.json node e2e/family-cameras-real.mjs`. El recorrido comprueba la incorporación de un miembro, la cámara privada inicial, el acceso del líder y del administrador, la reproducción de imágenes y la revocación. Usa una base de QA nueva para repetir el recorrido.
