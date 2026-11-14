# Borrador IA a pedido del cierre — 3.13 / ERP 3.17

## Ruta del ERP

`POST /api/v1/nutrition/cycles/{id}/review/ai-draft`

JWT ERP, `Nutrition.Approve`, `Nutrition.NotesView` y `Nutrition.RecordsView`.
`IPatientScopeGuard` comprueba el ciclo y paciente asignado antes de leer y tras
la llamada externa. Un ciclo ajeno devuelve 404; Admin conserva la comprobación
de permisos. No se necesita permiso para fotos: no se consultan ni envían.

```json
{ "revision": 0, "language": "es" }
```

Ambos campos tienen esos valores por defecto; `revision` debe ser no negativa e
igual a la revisión actual (0 si no existe). Idiomas: `es` / `en`. Se exige cuerpo
JSON. Un ciclo sin publicar o aún no iniciado devuelve 422. Una revisión publicada
u obsoleta devuelve 409; el editor debe recargar antes de reintentar.

HTTP 200 devuelve siempre estos campos (los null se conservan):

| Campo | Contrato |
| --- | --- |
| status | `ok`, `insufficient` o `unavailable` |
| revision | Nueva revisión si se guardó; la solicitada si no hubo escritura |
| aiDraft | Borrador trazable, o null en `unavailable` |
| warningCode / warning | Código estable y mensaje es/en, o null en `ok` |

`aiDraft` contiene `status`, `summary` (string), `patterns` y `gaps` (arrays de
strings), `draft: {es,en}`, `model`, `generatedAt` (UTC del servicio),
`capturedAt` (UTC de lectura), `inputHash` (SHA-256 hexadecimal minúsculo) e
`inputs` (request exacto enviado, descrito abajo). El hash se calcula sobre los
bytes UTF-8 del JSON camelCase enviado, incluidos sus null; no sobre el jsonb
reordenado. Se guarda todo en `nutrition_cycle_reviews.ai_draft_json`, sin nueva
migración. GET de la revisión devuelve este mismo objeto bajo `aiDraft` al
profesional con NotesView; nunca se incluye en recomendaciones de la app.

El resultado se guarda aumentando `revision` y poniendo `aiDraftConfirmed=false`.
No sustituye `professionalNotes`, `patientMessage`, alertas, resumen clínico ni
adjuntos y no publica. Crear el primer borrador IA también crea la revisión;
guardar/publicar la evaluación manual sigue usando las rutas de [3.9](CYCLE-REVIEWS.md).
En `ok`, el profesional revisa el texto, decide qué copiar al mensaje y confirma
expresamente el borrador actual antes de publicar. Otra generación invalida esa
confirmación. Las ediciones concurrentes se rechazan, no se pisan.

## Estados para la UI

| Estado | Persistencia / acción |
| --- | --- |
| Cargando | Permitir cancelar; no se mantiene una transacción durante la red |
| ok | Mostrar resumen, patrones, carencias y propuesta bilingüe; actualizar revision |
| insufficient | `warningCode=ai_insufficient_data`; guardar trazabilidad y actualizar revision; textos/arrays/model vacíos; continuar evaluación manual sin confirmar texto vacío |
| unavailable | `aiDraft=null`, sin escritura ni cambio de revision; conservar el formulario y permitir revisión manual/reintento |
| 409 | Recargar la revisión y conservar la edición local para resolver el conflicto |

En `unavailable`, los códigos son `ai_not_configured`, `ai_input_invalid`,
`ai_period_too_long`, `ai_timeout`, `ai_circuit_open` o `ai_unavailable`. El mensaje
es seguro y menciona que se puede continuar manualmente. No se devuelve cuerpo,
excepción ni detalle del proveedor. Un fallo tampoco borra un borrador previo.
Una cancelación solicitada por el cliente se propaga y no guarda el resultado.

## Datos reales hacia AI Service (contrato compacto 3.12)

Ruta interna `POST /internal/program/nutrition-cycle-summary`, sin `/api/v1`,
header servidor `X-Internal-Key`. Reutiliza configuración `AiService`, transporte
de consentimiento y correlación de la narrativa. El identificador del sujeto se
usa únicamente en las opciones del guard interno: jamás en JSON ni en el hash.

Request con exactamente estas nueve claves:

| Campo | Fuente |
| --- | --- |
| cycleNumber / startDate / endDate | Número publicado e intervalo atribuido por calendario 3.2 hasta hoy local, interrupción o día anterior al sucesor; incluye continuidad |
| language | Idioma solicitado para summary/patterns/gaps; draft siempre es/en |
| aggregates | daysWithRecords, adherence, avgCalories, avgProteinG, avgCarbsG, avgFatG, avgFiberG, avgWaterMl |
| mealCounts | Conteos completos de registros des/alm/mer/cen/agua en ese intervalo |
| previousCycleAggregates | Mismos agregados del ciclo numerado anterior y su intervalo atribuido, o null |
| measurements | Última medición clínica compatible de weight/waist/bmi/glucose, o null en cada valor ausente |
| records | Hasta 100 registros, últimos días primero, con desempate estable; date/mealCode/calories/macros/origin/waterMl |

Los agregados y conteos cubren **todos** los registros del intervalo, no solo la
muestra. `adherence` de este contrato es cobertura: días con registros / días
transcurridos atribuidos, redondeada a cuatro decimales. No modifica la fórmula
clínica D2 del endpoint de adherencia. Los promedios son sumas diarias medias
de valores conocidos; alimentos excluyen agua. Un dato desconocido queda null,
no cero. El origen solo es `ai_estimated` si los valores coinciden con el análisis
propio, o `patient_confirmed`; nunca se confía en una etiqueta clínica del cliente.

Mediciones: kg, cm, kg/m², mg/dL respectivamente; no se convierten unidades
incompatibles ni se calcula IMC desde otros valores. Se excluyen mediciones
posteriores al día atribuido o al instante de captura. No se inventan metas,
diagnósticos, nombres ni valores faltantes. Los registros no incluyen texto
libre, IDs de paciente/ciclo/registro/análisis, nombres, fotos, claves ni URLs.

El servicio admite hasta 366 días. Un período mayor devuelve `ai_period_too_long`;
valores reales fuera de sus límites devuelven `ai_input_invalid`, sin truncarlos
ni enviar un request inválido. Un anterior de más de 366 días se omite (null).
Response interna exacta: status `ok` / `insufficient`, summary, patterns, gaps,
draft, model, generatedAt. Se validan forma, textos, UTC y límite de 64 KiB.

## Resiliencia y comprobaciones

Timeout total acotado por `AiService.TimeoutSeconds` a 1..25 segundos, incluida
lectura de respuesta. Reutiliza `ProgramNarrativeResilience` compartido por host:
3 reintentos transitorios con esperas 2/4/8 s y circuito tras 5 fallos durante 30 s;
un circuito abierto devuelve aviso manual sin enviar otra llamada. La generación
es a pedido, fuera del worker y de cualquier transacción de escritura.

Pruebas: PostgreSQL y AI Service simulado mediante HTTP real en loopback para
mapear datos/privacidad/hash, fallo 503, timeout, respuesta inválida, insuficiencia,
publicación manual, edición concurrente, revocación y cancelación. Test HTTP de la
ruta ERP para binding, audiencia, permisos y caso ajeno; unitarios de contrato,
límite de cuerpo y circuito compartido. El request capturado también se valida
contra el esquema Pydantic del worktree ai-f3-3.12. Sin llamada a un LLM real.

Front 3.17: verificar con Playwright la pantalla de cierre, estados de la tabla,
ES/EN y 320/390/768 con consola limpia; esta tarea back verifica HTTP/DB, no UI.

Evidencia 3.13: build verde, unitarios **2622 aprobados/105 omitidos** y clase
completa PostgreSQL **NutritionPlanWorkflowTests 119/119**, sin omisiones ni
fallos (10 min 22 s), incluidos ambos ConservaPreedicion y la regresión Fase 1/F2.
Conexión local de test resuelta solo en memoria mediante COP_TEST_DB_CONNECTION.

```powershell
dotnet build --no-restore -p:OutputPath=bin/Validation313/ -v quiet
dotnet test tests/CoppAddresd.UnitTests --no-build -p:OutputPath=bin/Validation313/ -v normal
dotnet test tests/CoppAddresd.IntegrationTests --no-build -p:OutputPath=bin/Validation313/ --filter 'FullyQualifiedName~NutritionPlanWorkflowTests' -v normal
```
