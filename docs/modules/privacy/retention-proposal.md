# KAN-170 · Propuesta de retención para decisión

**Estado: propuesta operativa, no aprobada.** No cargar en producción ni
usar como autorización de borrado. El archivo
`scripts/qa/kan-170/retention-policy.pending.json` permanece sin aprobación.

## Propuesta

Separar el expediente clínico del perfil de acceso y los datos de bienestar.
Dar de baja el acceso sin alterar la historia que deba conservarse. Para datos
sin una dependencia clínica ni una suspensión vigente, usar la solicitud
verificada como punto de partida para la disposición. Las metas operativas de
la tabla no agregan una espera obligatoria ni sustituyen el plazo aplicable.

| Dato | Punto de partida y propuesta | Disposición y excepciones |
| --- | --- | --- |
| Identidad y perfil no clínico | Cierre de cuenta confirmado. Proponer completar disposición en hasta 30 días de operación, sin esperar los 90 días codificados en el flujo anterior. | Revocar acceso al confirmar la baja. Eliminar o anonimizar identificadores no requeridos. Separar facturación, prevención de fraude y vínculos clínicos sujetos a conservación demostrada. |
| Bienestar no clínico y conversaciones IA no incorporadas al expediente | Solicitud verificada. Proponer completar disposición en hasta 30 días de operación. | Eliminar datos y copias primarias del titular. Reconciliar memoria de IA, trazas y proveedores. Si un registro se incorporó al expediente o hay una suspensión, usar la regla de ese registro. |
| Signos vitales y expediente clínico | Última atención para expedientes a los que corresponda. No proponer un plazo uniforme sin determinar país, estado, custodio y origen del registro. | Conservar lo obligatorio y restringir su uso al cerrar la cuenta. Separar signos vitales de consumo que no integran expediente. No aplicar al expediente la meta de 30 días para datos no clínicos. |
| Logs operativos sin datos sensibles | Fecha de generación. Proponer 30 días para logs ordinarios. | Expirar. Separar evidencia de incidentes y auditar que el log no contenga datos clínicos o secretos. |
| Auditoría y solicitudes | Fecha de cierre del expediente de cumplimiento. Proponer 12 meses como hipótesis de producto, sujeto a revisión del plazo probatorio aplicable. | Conservar evidencia mínima sin cargas clínicas. Aplicar suspensiones documentadas y destruir cuando proceda. La copia de exportación mantiene la disponibilidad técnica de 24 h por defecto, no la retención probatoria. |
| Módulos retirados | Inventario, escrituras detenidas y restauración verificada por lote. | Conservar la regla del dato original al archivar. No reiniciar la retención por copiar ni eliminar sin manifiesto y verificación. |
| RDS automático | Siete días desde la copia, configuración observada. Proponer conservar la ventana, sujeto a revisión de continuidad y jurisdicción. | Expiración de copia, reconciliación de borrados al restaurar y registro de cuándo dejó de existir la última copia afectada. |
| Snapshots manuales y versiones de objetos | Proponer hasta 30 días a partir de la creación o de quedar no vigente, salvo necesidad justificada con fecha de vencimiento. | Inventariar y probar primero. No borrar snapshots ni activar lifecycle sobre el bucket vigente con esta propuesta. Gestionar copias de proveedores por separado. |

## Decisiones que faltan

- Qué jurisdicciones, entidades responsables y custodios aplican a cada
  conjunto de datos y si los vitales de consumo se incorporan al expediente.
- Qué conservación clínica, probatoria, financiera y por incidente aplica, qué
  suspensiones tienen precedencia y qué terceros deben recibir avisos o evidencias.
- Qué fechas de expiración son compatibles con restauración y continuidad en
  las copias manuales, versiones S3, réplicas y copias externas.
- Aprobador, referencia verificable y fecha efectiva. Los valores operativos
  anteriores no autorizan el modo destructivo ni cierran KAN-170.

HHS distingue la conservación de expedientes clínicos de HIPAA y señala que
en EE. UU. la conservación de esos expedientes suele depender de las leyes
estatales. Washington tiene requisitos propios de borrado y copias cuando
aplica su ley de datos de salud de consumidores. Colombia publica una regla
para el expediente clínico en el artículo 3 de la Resolución 839 de 2017.
Son referencias para clasificar datos, no una determinación de aplicabilidad.
[HHS](https://www.hhs.gov/hipaa/for-professionals/faq/does-hipaa-require-covered-entities-to-keep-medical-records-for-any-period/index.html) ·
[Washington RCW 19.373.040](https://app.leg.wa.gov/rcw/default.aspx?cite=19.373.040) ·
[Ministerio de Salud de Colombia, Resolución 839 de 2017](https://www.minsalud.gov.co/Normatividad_Nuevo/Resolucion%20No%20839%20de%202017.pdf)
