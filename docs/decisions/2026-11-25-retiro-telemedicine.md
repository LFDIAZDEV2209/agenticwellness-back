# Retiro controlado de telemedicina

**Decisión aprobada por el owner el 25 de noviembre de 2026.**

## Contexto

El ERP y la app dependen del microservicio de telemedicina para la funcionalidad de Citas. El servicio se desplegaba automáticamente mediante `deploy-backend.yml`.

## Decisión

Apagar el microservicio en AWS y retirar su despliegue automático. El repositorio conserva el código fuente, los proyectos de `CoppAddresd.slnx` y sus pruebas. El orquestador coordina el apagado y las operaciones sobre los datos.

## Consecuencias

- Citas del ERP y de la app quedarán fuera de servicio. El owner acepta este impacto.
- El orquestador respaldará en S3 los datos clínicos del esquema `tele` antes de eliminar el esquema.

## Reversión

Si el esquema `tele` ya se eliminó, restaurarlo y verificar los datos desde el respaldo en S3 antes de habilitar tráfico. Redesplegar el servicio en ECS desde el código conservado usando la definición de tarea previa como base. Validar Citas en el ERP y en la app, y reincorporar el servicio al workflow si se restablece el despliegue automático.
