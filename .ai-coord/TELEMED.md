# Retiro de telemedicina · carril backend

Rama `feat/retiro-telemedicine-deploy` desde `origin/dev` (`e8ca8268`).

## Resumen del diff

- `.github/workflows/deploy-backend.yml` excluye telemedicina de la detección, la matriz, la selección de pruebas, el mapeo Dockerfile/ECR, las migraciones y el warmup. Los cambios compartidos y las ejecuciones manuales siguen seleccionando `auth`, `api` y `community`. Se conserva la regla compartida `Dockerfile`, por lo que un cambio al Dockerfile de telemedicina puede seleccionar esos tres servicios.
- `docs/decisions/2026-11-25-retiro-telemedicine.md` registra la decisión del owner, sus consecuencias y la reversión. `docs/modules/telemedicine/README.md` enlaza el ADR desde la nota de estado.
- Se conservan el código fuente, las referencias, los proyectos de `CoppAddresd.slnx` y las pruebas de telemedicina.

## Gates

- `dotnet build CoppAddresd.slnx --disable-build-servers` en curso.
- YAML válido con Python y PyYAML 6.0.4. El detector también pasó `bash -n`.
- Matriz y selección de pruebas verificadas en 13 escenarios con el detector del workflow. La revisión completa confirmó cero menciones de `telemedicine` en el YAML. Las migraciones quedaron limitadas a `api`.

## Riesgos y coordinación

- Citas del ERP y de la app quedarán fuera de servicio al apagar el servicio. El owner aceptó ese impacto.
- El orquestador coordina el apagado, el respaldo en S3 y la eliminación posterior del esquema `tele`. Si ya se eliminó el esquema, la reversión depende de restaurar y verificar el respaldo antes de habilitar tráfico.
- Cambios locales en el worktree, sin commit ni push. Las operaciones en AWS y en la base de datos corresponden al orquestador.
