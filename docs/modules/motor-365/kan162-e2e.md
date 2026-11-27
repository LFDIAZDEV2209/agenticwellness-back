# KAN-162 · Pruebas de aceptación del ajuste de plan con IA

La suite primero comprueba el plan determinista en Antares. Los casos de
aceptación requieren que una propuesta pase por el servidor, deje una decisión
auditable y nunca modifique el cálculo base. El proveedor se sustituye por un
simulador HTTP local que solo devuelve datos sintéticos. No valida ni persiste
propuestas. Se usan Antares, los controladores del Motor 365 y PostgreSQL
reales, con el fixture existente de autenticación, hora y paciente.

## Cobertura

| Caso | Evidencia que deberá producir el flujo |
| --- | --- |
| Control sin IA | Antares muestra kcal, macros y sesión tal como los devuelve el Motor 365. No hay llamadas al simulador. |
| Propuesta permitida | La sesión se acorta y los objetivos de nutrición cambian sin perder coherencia con el plan. Un navegador nuevo ve el resultado y su motivo. La auditoría guarda campos propuestos y aplicados, motivo y versiones. |
| Reversión | Un navegador nuevo puede recuperar exactamente la nutrición, la sesión y el menú de referencia. La decisión original se conserva con un evento de reversión. El endpoint canónico del motor recalcula la misma base antes, durante y después del ajuste. |
| Límites | Se rechazan por separado el piso y tope de kcal, el tope de proteína, el piso de carbohidratos, un nivel no disponible y la duración fuera de rango. Una propuesta mixta con una duración inválida se descarta completa. |
| Vigencia y seguridad | Una versión de parámetros distinta de la vigente o una indicación clínica en el motivo no se aplican. El texto clínico rechazado no llega a la decisión visible ni al historial consultable. |
| Indisponibilidad | Un 503 del simulador conserva la dieta y el ejercicio base. Se presenta el motivo y se registra el intento sin aplicar cambios. |
| Autorización y privacidad | Otro paciente no puede iniciar un ajuste. Solo el personal ERP con alcance puede leer la auditoría. Playwright compara el cuerpo enviado al simulador con la lista exacta de campos permitidos. |
| Cambio durante la solicitud | Un cierre del día mientras responde el simulador invalida la propuesta. Antares muestra el rechazo y ya no ofrece ajustes en ese día cerrado. |
| Entrada inválida | Un valor numérico que desborda el tipo se rechaza, sin persistirlo ni alterar el plan. |

## Contrato del ajuste local

- En el plan de Antares, una acción explícita solicita el ajuste del día
  actual. El POST a `/api/v1/people/me/plan/{day}/ai-adjustment` no recibe
  valores de nutrición ni ejercicio del navegador. El servidor obtiene
  la propuesta. La lectura ordinaria del plan no llama al proveedor.
- Los casos de prueba esperan una decisión con estado, código de rechazo
  cuando corresponda, motivo apto para mostrar y ambas versiones.
  `DELETE` en la misma ruta revierte el ajuste sin borrar la decisión.
- La lectura del día para Antares presenta el plan efectivo. La lectura
  canónica bajo `/v1/people/{id}/plan/{day}` permanece determinista.
  Un profesional autorizado consulta las decisiones en
  `/api/v1/people/{id}/plan/{day}/ai-adjustment/audit`.
- La auditoría requiere audiencia ERP y el permiso y alcance del Perfil
  360. Está ordenada por `sequence`; para continuar, usa `after` con la última
  secuencia y `limit` (20 por defecto, hasta 100).
- La API solo acepta propuestas de un adaptador local optativo. Sin
  `Motor365Ai__EnableLocalProposals=true` y una URL HTTP en `127.0.0.1`,
  no se ofrece la acción. El simulador recibe únicamente los objetivos
  numéricos y las versiones. No recibe nombre, ID, registro ni síntomas.
- La propuesta contiene cambios tipados y un código de motivo:
  `shorter_session`, `nutrition_balance` o `daily_consistency`. El servidor
  publica su propia traducción. Un motivo desconocido se descarta y
  nunca queda en el historial como texto libre.

El ajuste vive aparte del cálculo base. Solo se aplica en el día actual
abierto y sin pausa, mientras coincidan perfil, revisión y parámetros
validados. No puede elevar la dosis de ejercicio ni cambiar el catálogo.
La energía queda dentro del piso y el rango de la fase del motor, y los
macros respetan el piso de carbohidratos, el tope de proteína, la meta de
grasa y su presupuesto energético. Si una propuesta mezcla cambios
válidos e inválidos, se descarta completa. El menú se vuelve a generar
con los objetivos efectivos. Vita y KAN-164 no reciben la capacidad de
editar el motor ni cambian su política de entrega.

No se añadió ni se habilitó una integración con un proveedor remoto.
La retención y la aprobación de privacidad quedan fuera de esta suite.
[KAN-165](https://diomedescerda.atlassian.net/browse/KAN-165)

## Reproducir

Instala las dependencias de Antares en un checkout separado. Si el lockfile
de referencia no coincide con `package.json`, se puede usar
`npm install --no-package-lock` en ese checkout sin editar archivos
versionados. Para el runner, desde `tests/CoppAddresd.Wellness365.E2E`:

```sh
npm ci
npx playwright install chromium
export COP_KAN130_APP_DIR=/ruta/absoluta/a/antares-paciente
export COP_KAN131_TEST_DB_CONNECTION='Host=127.0.0.1;Port=55434;Database=copp_motor365_test_kan162;Username=postgres'
npm run test:kan162
```

La conexión debe apuntar a PostgreSQL local descartable, en un puerto distinto
de 5432 y con una base que empiece por `copp_motor365_test_`. El fixture
crea los esquemas y aplica las migraciones del motor que necesita. Si el SDK
local devuelve `NETSDK1226`, compila con
`dotnet build -c Release -p:AllowMissingPrunePackageData=true` desde el mismo
directorio y vuelve a ejecutar la suite con `COP_KAN162_NO_BUILD=1`.

Para ejecutar solo el control, usa `npm run test:kan162 -- --grep 'control ·'`.

## Resultado de la fase test-first

Verificado en Chromium con backend `60611d03` y Antares `405786c`. Pasó
el control del plan base y los once casos de aceptación quedaron en rojo.
El POST del contrato devolvió 404 y los otros diez casos se detuvieron
porque la acción de ajuste aún no existe en Antares. No se alcanzaron las
aserciones de validación, persistencia y reversión de la funcionalidad
nueva durante esa primera fase.

## Verificación de la implementación

La suite final en Chromium pasó los 15 escenarios sin omisiones ni
reintentos, sobre el backend de referencia `1867d62a` y Antares
`405786c` más los cambios locales. Pasó la regresión E2E de KAN-132.
Se aprobaron 108 pruebas de backend del módulo Wellness 365 y 272
pruebas de Antares. En PostgreSQL descartable se probó el rollback
y reaplicación de la migración, y que el trigger rechaza UPDATE, DELETE
y TRUNCATE de la bitácora. No se usó un proveedor externo.
