# Ayuda al cargar podcasts — 4.6b

Contrato interno fijado en **ai-service a977e33**, `docs/podcast-metadata.md` y
`app/schemas/podcast_metadata.py`. Ayuda editorial a pedido en Biblioteca; devuelve
una estimación editable. Solo el backend consume AI. No crea, guarda, edita ni
publica medios; no descarga ni transcribe audio.

## API ERP

`POST /api/v1/media/podcast-metadata`

```json
{
  "title": "Crear hábitos sostenibles",
  "language": "es",
  "context": "Introducción a la constancia y los pequeños pasos."
}
```

Audiencia ERP + **Media.Create y Program.AiManage** efectivos en el contexto activo,
usando las mismas capacidades de la Biblioteca y la IA existente. Matriz §10.1:
Admin y ClinicalDirector pueden solicitar; NutritionReviewer y CareCoordinator
solo leen la Biblioteca. Admin también necesita ambos permisos. No requiere
Media.Publish: aquí no hay publicación. Permisos comprobados antes y después de
la llamada. Es catálogo global, sin recurso ni alcance de un paciente.

Título obligatorio de 1–200 caracteres; language es/en, predeterminado es;
context opcional hasta 16000, predeterminado vacío. Texto plano sin HTML ni URLs;
se recortan espacios exteriores antes de enviar. JSON con campos extra se rechaza:
no admite patientId, mediaId, storageKey, estado, archivos ni audio. El operador
debe seleccionar únicamente texto editorial sin datos de pacientes, credenciales
ni enlaces firmados; no existe una clasificación automática de datos sensibles
dentro de prosa libre. Tamaño máximo del request: 128 KiB.

401 anónimo; 403 audiencia o capacidades ausentes; 400 texto/idioma/límites/campos
inválidos. GET no genera: la ruta solo admite POST. Respuestas `Cache-Control: no-store`.

HTTP 200 correcto:

```json
{
  "status": "ok",
  "isEstimate": true,
  "draft": {
    "description": "Una introducción a la constancia y los pequeños pasos para construir hábitos cotidianos.",
    "category": "Habitos",
    "tags": ["constancia", "hábitos", "pequeños pasos"]
  },
  "model": "modelo-configurado-o-null",
  "generatedAt": "2026-11-15T20:00:00Z"
}
```

Description no vacío ≤2000. Category es un código estable del enum MediaCategory:
Biologia, Nutricion, Psicologia, CrecimientoPersonal, Habitos, SaludFisica,
BienestarEmocional, Mindfulness o Motivacion; nunca número ni traducción. Tags es
un array de 0–32 textos de 1–64 caracteres: normalización del catálogo existente
(trim, minúsculas invariantes, unicidad, orden). Model nullable, máximo 200;
generatedAt UTC del servicio. Sin inventar modelo si está ausente.

Fallo de AI, timeout, circuito abierto o salida inválida:

```json
{
  "status": "unavailable",
  "isEstimate": true,
  "draft": null,
  "model": null,
  "generatedAt": null
}
```

El consumidor omite la ayuda y conserva todos los valores ya escritos. Debe
mostrar «estimado por IA», modelo/fecha disponibles y cada campo como texto
editable; aceptar/editar/guardar requiere acción humana separada. No reemplazar
automáticamente campos al abrir o al recibir una respuesta tardía. Crear/editar
y publicar siguen usando los endpoints y permisos existentes de Media.

## Fronteras y reutilización

SuggestPodcastMetadataCommand pasa por el pipeline FluentValidation y usa
IPodcastMetadataClient; no depende de un repositorio de medios, storage, pacientes
ni publicación. El cliente manda exactamente title/language/context a
`POST /internal/media/podcast-metadata` con X-Internal-Key, usando configuración
AiService, correlación y ProgramNarrativeResilience existentes. Sin proveedor,
motor, cola, flag ni persistencia nueva; no usa patient_ai_briefs porque no es un
borrador de un paciente. No hay migraciones, índices ni agregaciones/KPI nuevos;
no corresponde otro pipeline CQRS.

AiConsentHttpHandler conserva su política existente de operador ERP autenticado;
no se crea ni se elude un consentimiento de un paciente, pues no hay sujeto ni
datos de pacientes en este caso de uso. No se ejecuta en background ni se exponen
claves en el JSON. El handler exige identidad y capacidades antes de usar AI.

Timeout total configurable acotado a 1..25 s, incluyendo lectura del cuerpo
(AI tiene su timeout de 20 s); respuesta máxima 64 KiB. JSON estricto tanto en
raíz como en draft, límites/estimación/UTC/categoría/campos comprobados; rechaza
HTML y URLs en descripción/etiquetas. Reintentos y circuito compartidos existentes.
Cancelación del caller se propaga, sin convertirla en éxito ni devolver un draft.
Logs propios solo contienen evento, duración y tipo de fallo; sin título/contexto,
respuesta del proveedor ni sus excepciones internas. La validación no certifica
el contenido ni acredita que el sistema haya escuchado el episodio.

## Evidencia local — 15-11-2026

- Build **0 errores**; unitarios backend **2795 aprobados / 105 omitidos**, incluidos
  37 casos nuevos: audiencia, permisos individuales y matriz de roles, petición
  estricta, ES/EN/defaults, no-store, límites, categorías, normalización,
  JSON inválido/extras, HTML/URLs, modelo/UTC, timeout/cancelación y degradación.
- PostgreSQL aislado **12/12**, sin omitidos: HTTP real hacia AI simulado, comparación
  de catálogo/snapshots/briefs/bitácora antes y después, modelo200/null, timeout de
  cuerpo y cancelación; regresión de medios externos, filtros/paginación,
  migración existente, lotes, sustitución/retiro y eliminación con referencias.
  Ninguna operación de sugerencia modifica o publica medios ni altera planes.
- Request capturado por HTTP validado con Pydantic del commit exacto **a977e33**:
  tres campos, idioma es, contexto de 52 caracteres. No se envían IDs ni storage.
- Simulación Postman con el cliente .NET real: **200 ES, 200 EN, 503**, tres
  requests/aserciones aprobados. Mock construido desde el contrato; búsqueda de
  workspace propietario omitida porque CLI 1.76.0 no reconoce `postman status`.
  No se inicializó ni publicó workspace. Fuente y límites en
  `postman/simulations/podcast-metadata.context.yaml`.

No se invocó un LLM live ni se arrancó/reinició el stack compartido. QA visual y
prueba live del consumidor quedan al orquestador/ERP. La simulación propia se
cierra al finalizar.

```powershell
dotnet build --no-restore -p:OutputPath=bin/Validation46b/ -v quiet
dotnet test tests/CoppAddresd.UnitTests --no-build -p:OutputPath=bin/Validation46b/ -v quiet
# COP_TEST_DB_CONNECTION local: los fixtures crean y eliminan BD independientes.
dotnet test tests/CoppAddresd.IntegrationTests --no-build -p:OutputPath=bin/Validation46b/ --filter 'FullyQualifiedName~PodcastMetadata_|FullyQualifiedName~MediaIntegrationTests|FullyQualifiedName~MediosExternos_|FullyQualifiedName~MediosOrigenTags_|FullyQualifiedName~LoteMedios_|FullyQualifiedName~Retiro_' -v quiet
```

Simulación propia en background y cierre verificado por PID al acabar:

```powershell
postman simulation run postman/simulations/podcast-metadata.sim.yaml --port 4526 --output ndjson --no-history
$env:COP_PODCAST_METADATA_SIM_URL='http://127.0.0.1:4526'
dotnet test tests/CoppAddresd.UnitTests --no-build -p:OutputPath=bin/Validation46b/ --filter FullyQualifiedName~PodcastCliente_ContratoYNormalizacion -v quiet
```
