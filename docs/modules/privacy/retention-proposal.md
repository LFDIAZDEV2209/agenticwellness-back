# KAN-170 · Decisión operativa de retención

**Decisión de producto de Diomedes, 27-11-2026.** [Registro en Jira](https://diomedescerda.atlassian.net/browse/KAN-170?focusedCommentId=10471).
Sirve para implementar y verificar. No certifica la aplicabilidad jurídica
por fuente ni habilita borrado en producción. La política ejecutable en
`scripts/qa/kan-170/retention-policy.pending.json` no cambia.

## Reglas acordadas

Separar el expediente clínico del perfil de acceso y los datos de bienestar.
Dar de baja el acceso sin alterar la historia que deba conservarse. Para datos
sin una dependencia clínica ni una suspensión vigente, usar la solicitud
verificada como punto de partida para la meta de disposición. Registrar
recepción y verificación por separado. Los 90 días son un plazo máximo
operativo por defecto, no 90 días de espera. Si aplica un plazo anterior,
prevalecen su fecha límite y su propio punto de partida.

| Dato | Punto de partida y regla | Disposición y excepciones |
| --- | --- | --- |
| Identidad y perfil no clínico | Hasta 90 días calendario desde la verificación, sujeto a un plazo anterior aplicable. | Revocar el acceso de paciente al confirmar la baja sin afectar un acceso ERP independiente. Eliminar identificadores elegibles. Separar facturación, fraude y vínculos clínicos que requieran conservación. |
| Bienestar, vitales de consumo e IA fuera del expediente | Mismo plazo operativo de hasta 90 días desde la verificación. No fija una caducidad para datos de usuarios activos sin solicitud. | Borrar los registros elegibles y conciliar memoria, trazas y copias en proveedores. Registrar aviso y confirmación del destinatario. Una copia incorporada al expediente sigue su propia regla. |
| Expediente clínico y excepciones | Regla del custodio y jurisdicción. Para expedientes colombianos cubiertos por la Resolución 839, el mínimo general es de 15 años desde la última atención. | No aplicar el mínimo colombiano a expedientes de EE. UU. o a vitales de consumo. Revisar por registro cuando haya dudas. Documentar motivo, responsable y fecha de revisión de cada excepción. Continuar con los registros elegibles del mismo titular. |
| Logs operativos ordinarios | 30 días desde la generación. | Expirar. Separar incidentes con conservación documentada. No incluir datos clínicos, conversaciones ni secretos en logs ordinarios. |
| Auditoría y solicitudes | 12 meses desde el cierre, salvo obligación superior. | Conservar evidencia mínima, sin copia clínica ni archivo exportado. La copia de exportación está disponible 24 horas desde que queda lista. |
| Módulos retirados | Detener escrituras, inventariar, archivar y verificar la restauración antes de retirar la copia activa. | La copia conserva la regla y el punto de partida del dato original. Un archivo no reinicia su retención. |
| RDS automático | Mantener 7 días de ventana automática. | Reconciliar los borrados antes de reabrir un entorno restaurado. Registrar la expiración de la última copia afectada aparte del borrado en primario. |
| Snapshots manuales y versiones de objetos | 30 días desde la creación del snapshot o desde que una versión S3 deja de ser actual, salvo excepción documentada. | Inventariar primero. Aplicar expiración solo a copias elegibles, con plazos anteriores si corresponden. No es una regla de borrado de archivos actuales ni de eliminación masiva de snapshots. |

## Evidencia de implementación y cierre que falta

- Qué jurisdicciones, entidades responsables y custodios aplican a cada
  conjunto de datos y si los vitales de consumo se incorporan al expediente.
- Qué conservación clínica, probatoria, financiera y por incidente aplica, qué
  suspensiones tienen precedencia y qué terceros deben recibir avisos o evidencias.
- Qué fechas de expiración son compatibles con restauración y continuidad en
  las copias manuales, versiones S3, réplicas y copias externas.
- Verificación con datos sintéticos de tres escenarios. Una cuenta ordinaria
  pierde solo los datos elegibles. Un expediente clínico protegido se conserva
  aunque se borre lo demás. Una restauración anterior al borrado no reabre
  acceso hasta que se concilien las eliminaciones.
- El preflight actual interpreta `days` como una retención mínima a partir de
  `anchorAt`. No usar ese campo para el objetivo de 90 días. La implementación
  separará elegibilidad de borrado, fecha límite y retención obligatoria. Un
  caso confirmado y elegible antes del día 90 no se debe posponer.
- Verificar la aplicabilidad jurídica por fuente y dejar evidencia antes de
  habilitar ejecución destructiva o cerrar KAN-170.

HHS distingue la conservación de expedientes clínicos de HIPAA y señala que
en EE. UU. la conservación de esos expedientes suele depender de las leyes
estatales. Washington tiene requisitos propios de borrado y copias cuando
aplica su ley de datos de salud de consumidores. Colombia publica una regla
para el expediente clínico en el artículo 3 de la Resolución 839 de 2017.
Son referencias para clasificar datos, no una determinación de aplicabilidad.
[HHS](https://www.hhs.gov/hipaa/for-professionals/faq/does-hipaa-require-covered-entities-to-keep-medical-records-for-any-period/index.html) ·
[Washington RCW 19.373.040](https://app.leg.wa.gov/rcw/default.aspx?cite=19.373.040) ·
[Colombia Ley 1581, art. 15](https://www.cancilleria.gov.co/normograma/compilacion/docs/ley_1581_2012.htm) ·
[Ministerio de Salud de Colombia, Resolución 839 de 2017](https://www.minsalud.gov.co/Normatividad_Nuevo/Resolucion%20No%20839%20de%202017.pdf)
