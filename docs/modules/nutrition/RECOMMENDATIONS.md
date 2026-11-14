# Recomendaciones del paciente — 3.10 / D0014

`GET /api/v1/me/nutrition/cycles/recommendations` requiere JWT de la app
(`aud=app`). No recibe IDs de paciente, usuario, ciclo o programa. `ICurrentContext`
aporta el usuario autenticado y `IPatientScopeGuard.EnsureSelfAsync` resuelve su
perfil no eliminado. Otra identidad, incluso con rol Admin, no permite elegir un
paciente ajeno. No exige permisos ni asignación ERP para leer datos propios.
Sin perfil → 404; sin publicaciones → 200 con `{ current: null, previous: [] }`.
Sin autenticación → 401; cliente ERP → 403.

Contrato público exacto, compatible con `nutrition-cycle-recommendation-types.ts`
del carril app:

```json
{
  "current": {
    "cycleNumber": 1,
    "startDate": "2026-11-02",
    "endDate": "2026-12-01",
    "publishedAt": "2026-12-03T18:00:00Z",
    "professionalName": "Nombre del profesional",
    "patientMessage": "Mensaje aprobado para el paciente",
    "attachments": [{ "url": "https://api/…?exp=…&sig=…", "caption": "Ejemplo" }]
  },
  "previous": []
}
```

**Matiz aprobado por Luis:** el mensaje se almacena en el ciclo n revisado;
`current` es la publicación más reciente por `published_at`, aunque ese ciclo
terminara. `previous` son las demás publicaciones en orden descendente por la
misma fecha (UUID como desempate estable). No se ordena por número ni se exige
vigencia hoy. El plan n+1 puede mostrar así «Cierre del ciclo n». No cambia el
almacenamiento de 3.9 ni los contratos de 3.1–3.4.

`patientMessage` es texto, no un objeto bilingüe. Se selecciona el texto es/en
aprobado y almacenado: `X-App-Language: en` o `?language=en` habilitan inglés
mediante el resolver existente de la app; español por defecto. No se llama a IA
para traducir una recomendación publicada. `Content-Language` y `Vary` reflejan
la selección; respuesta `private/no-store`. El pie conserva su texto aprobado
(vacío si no hay pie), sin traducción automática.

Solo adjuntos `visible_to_patient=true` y `kind=image`, con URL de cinco minutos
del controlador privado de 3.9. Documentos, imágenes internas y revisiones en
borrador no se consultan para el DTO. La proyección SQL ni siquiera carga
`professional_notes`, `ai_draft_json`, `summary_json` o `storage_key`; no expone
IDs de revisión ni contexto de programa. No consulta por cada archivo: una
consulta de identidad y dos proyecciones por conjuntos. Las dos proyecciones
comparten Repeatable Read READ ONLY, y las firmas se generan después.

Sin migración. La ampliación del guard separa explícitamente autoservicio de
los permisos/asignación del ERP; mantiene todas las comprobaciones anteriores.

```powershell
dotnet build --no-restore -p:OutputPath=bin/Validation310/ -v quiet
dotnet test tests/CoppAddresd.UnitTests --no-build -p:OutputPath=bin/Validation310/ -v normal
dotnet test tests/CoppAddresd.IntegrationTests --no-build -p:OutputPath=bin/Validation310/ --filter 'FullyQualifiedName~NutritionPlanWorkflowTests' -v normal
```

Pruebas: HTTP real en TestServer (401/403, IDs del cliente ignorados, ES/EN,
envoltorio vacío, no-store); PostgreSQL con ciclos terminados, orden por
publicación distinto del número, DTO exacto/ausencia de campos internos,
imágenes visibles frente a internas/documentos/borradores, firma temporal,
identidad propia/ajena/Admin, perfil eliminado, cancelación, tres consultas y
ausencia de escrituras. UI real pendiente de los carriles front/app mediante
Playwright (flujo completo, vacío/error/carga, 320/390/768 y consola).

Evidencia 3.10: build verde, unitarios **2600 aprobados/105 omitidos**;
PostgreSQL **115/115**, suite completa en 12 min20s, con Fase 1/F2 y ambas
ConservaPreedicion. HTTP real del contrato en TestServer incluido en los
unitarios; el caso PostgreSQL verifica además las columnas SQL no consultadas,
el número constante de consultas y la firma temporal de los adjuntos permitidos.
