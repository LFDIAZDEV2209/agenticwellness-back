# Motor 365 · Día local y estado del plan

[KAN-131](https://diomedescerda.atlassian.net/browse/KAN-131) agrega el
servicio de aplicación y la persistencia de calendario y cierre. Este tramo
de [KAN-130](https://diomedescerda.atlassian.net/browse/KAN-130) conecta la
lectura autenticada a Inicio de App y a Perfil 360 del ERP. El resumen no
entrega recomendaciones y el worker de generación no cambia.

## Calendario y registros

- Al activar el plan real, el servidor obtiene la fecha civil en la zona
  de la persona a partir de su reloj UTC. Ignora el inicio y el modo
  enviados por el cliente. El inicio original no vuelve a cambiar.
- El día de plan es la diferencia de fechas civiles más uno, dentro de
  1–365. Tras un cambio de zona, se conserva el día efectivo en ese
  instante y se usa como ancla la fecha civil de la nueva zona. El día
  avanza en la siguiente medianoche local, aunque el intervalo tenga
  23 o 25 horas. No se renumeran los registros previos.
- El inicio original y las tres columnas del ancla viven en
  `app.person`. Si aún no tiene ancla, se toma el día 1, el inicio
  persistido y la zona del perfil canónico. El alta del motor fija y
  persiste el ancla inicial.
- La lectura y el cierre usan la transacción del repositorio canónico.
  Al detectar un cambio en la zona del perfil se conserva el ordinal
  y se persiste el ancla en la nueva zona. El cierre escribe usando `(person_id, day)`
  y asigna a `log_date` la fecha nominal del plan, inicio más día menos
  uno. Esa fecha no se reescribe y no sirve para decidir si el día es
  futuro. Así se mantiene el índice único de KAN-134 incluso si,
  después de viajar al oeste, dos ordinales tienen la misma fecha civil.
  Las escrituras efectivas avanzan la versión y registran auditoría
  en la misma transacción. Repetir el mismo cierre no crea otra fila,
  versión ni evento de auditoría.
- En modo real, un día pasado sin registro tiene `logStatus=missing`,
  adherencia de cálculo 0,8 y ningún pesaje. Un día en curso no registrado
  tiene `logStatus=open`. Las proyecciones internas no se exponen como
  mediciones. Solo los días registrados con adherencia de al menos 0,6
  cuentan para la racha. Un faltante pasado la corta y un día abierto
  aún no la modifica.
- El demo es una elección explícita del host de pruebas. Acepta un inicio
  de prueba y etiqueta su historia sintética como `simulated`. No hay
  opción de activarlo por solicitud en el servicio real.

## Estado y errores

`locked` conserva el motivo de la compuerta. Un día disponible se
presenta como `active`, `paused` si el motor indica pausa por síntoma,
o `maint` si se alcanzó la meta. Durante una pausa no se entrega una
sesión de ejercicio. En real la meta se confirma con el peso inicial
registrado o un pesaje de un día cerrado, nunca por la estimación del
motor. El estado resultante aplica al calcular el día siguiente.

| HTTP | Error | Condición |
| --- | --- | --- |
| 404 | `person_not_found` | No hay persona en el alcance autorizado. |
| 400 | `bad_day` | Día explícito no entero o fuera del rango 1–365. |
| 400 | `bad_log` | Valores de cierre fuera de rango o síntomas desconocidos. |
| 409 | `plan_locked` | El plan no se puede leer. El motivo va en `status.code`. |
| 422 | `future_day` | Lectura de un plan disponible o cierre posterior al día local. |

El cierre usa el calendario activo. La compuerta de lectura no impide
recibir un registro de un plan ya iniciado, como en el contrato del kit.

## Integración y verificación

La app lee `GET /api/v1/people/me/plan` y registra el cierre por
`PUT /api/v1/people/me/logs/{day}`. El cierre conserva el ordinal recibido
del servidor cuando queda pendiente y se reintenta en la misma cuenta.
El ERP lee `GET /api/v1/people/{personId}/plan` con
`Program.Profile360View` y el alcance clínico del paciente. Ambos pueden
leer un día específico con `/plan/{day}`. Las rutas de cliente solo
aceptan cierres desde la app.
Las rutas de cliente reutilizan los casos de uso del contrato canónico
`/v1` de KAN-130, sin duplicar escrituras. El resumen expone día local,
estado, racha y pesaje registrado, sin recomendaciones. El contrato
canónico conserva el plan anual, pero devuelve `w: null` cuando no hubo
pesaje y rechaza la lectura de un día futuro específico. Las rutas de
cliente comprueban membresía y consentimientos vigentes. El contrato
canónico valida los recibos vinculados y, para la lectura del plan,
también las respuestas y la revisión profesional cuando corresponde.

`AddMotor365Calendar` agrega el ancla sobre las tablas de
[KAN-134](https://diomedescerda.atlassian.net/browse/KAN-134), sin reescribir
filas o modificar la migración aplicada. El rollback conserva los cierres
y elimina solo el ancla de la que depende el calendario después de un
cambio de zona.

Estas rutas consumen filas reales de `app.person` y `app.screening`.
No convierten la generación de demostración ni aprovisionan pacientes.
El alta y el contrato de planes de KAN-130 se conservan. Este tramo no
resuelve las diferencias
del kit registradas en el [ADR](../../decisions/2026-11-24-motor-365-dotnet.md#diferencias-detectadas-entre-la-guía-y-la-referencia-ejecutable).

Las pruebas usan un host aislado, un reloj controlado y datos sintéticos.
Comprueban la lectura desde Chromium y el contrato HTTP del mismo servicio
que integrará la API. No sustituyen las pruebas con JWT, PostgreSQL, App
y ERP del contrato desplegado.

```sh
cd tests/CoppAddresd.Wellness365.E2E
npm ci
npx playwright install chromium
npm test
```

En una instalación local de .NET sin datos de poda de paquetes, ejecutar
`AllowMissingPrunePackageData=true npm test`.

Para incluir el sexto caso, crear un PostgreSQL efímero solo en loopback
y un puerto distinto de 5432, con una base vacía llamada
`copp_motor365_test_kan131`. Configurar
`COP_KAN131_TEST_DB_CONNECTION` con esa conexión antes de `npm test`.
El host aplica solo el tramo de migraciones del Motor 365 sobre
prerrequisitos sintéticos. La prueba vacía el estado en memoria, vuelve
a leer el calendario y el pesaje de PostgreSQL y verifica dos intentos
concurrentes del mismo cierre y sus efectos de auditoría.

### Prueba de los dos clientes

Con la base efímera anterior, definir además `COP_KAN130_APP_DIR` y
`COP_KAN130_ERP_DIR` con los checkouts locales de los clientes y sus
dependencias instaladas. Playwright inicia Vite, Next y el host de pruebas.
Sustituye la restauración de sesión y las consultas ajenas al Motor 365 por
datos sintéticos, pero pasa JWT firmados a las rutas reales del controlador.
El día y el cierre se leen y escriben en PostgreSQL. No prueba Gateway,
el proveedor de identidad, recibos legales publicados ni cuentas reales.

`AllowMotor365NauseaObservation` conserva el síntoma náuseas del formulario
existente como observación. No añade una regla al motor. Al revertir, la
restricción anterior se aplica a nuevas escrituras sin borrar registros ya
guardados con esa observación.
