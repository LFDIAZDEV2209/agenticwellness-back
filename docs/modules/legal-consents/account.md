# Términos y Privacidad de la cuenta

El catálogo de Documentos legales del ERP determina la versión vigente de cada documento. Auth y API deben recibir el mismo mapa de códigos por idioma. El mapa identifica dos documentos distintos y ambos deben tener una versión publicada con contenido.

| Ajuste | Uso |
| --- | --- |
| AccountLegalConsents:EnforcementEnabled | Habilita la comprobación. Está apagada por defecto. El estado deshabilitado devuelve la lista vacía. |
| AccountLegalConsents:Documents:es:Terms | Código de Términos en español. |
| AccountLegalConsents:Documents:es:Privacy | Código de Privacidad en español. |
| AccountLegalConsents:Documents:en:Terms | Código de Términos en inglés. |
| AccountLegalConsents:Documents:en:Privacy | Código de Privacidad en inglés. |

La activación simulada usa los códigos de las semillas de prueba que correspondan al entorno. Para la habilitación con clientes reales, usar los códigos aprobados de ambos documentos en el ERP.

## Recibo y vigencia

- Un recibo de cuenta usa los ámbitos *account-terms* o *account-privacy*. Conserva el identificador de cuenta, el de la versión publicada, el idioma y la fecha UTC del servidor. El campo JSON *disclosure_snapshot* contiene la IP y el agente de usuario de la solicitud. El agente de usuario se limita a 512 caracteres. Los recibos anteriores conservan sus referencias y metadatos.
- Una aceptación vigente pertenece a la cuenta, el ámbito, el idioma y la versión publicada correspondiente. La publicación de una versión actualiza el puntero del documento. Los recibos de la versión previa siguen consultables.
- Los recibos del alta demo anterior cuentan para el mismo documento demo si coinciden la versión y el idioma. La fila conserva su ámbito original al volver a consultar o aceptar.
- La escritura serializa por cuenta y bloquea en lectura los documentos que se están aceptando hasta terminar la transacción. Una versión ajena u obsoleta devuelve conflicto. Un reintento de la misma escritura, o la aceptación concurrente de la misma versión, devuelve el recibo existente.
- La publicación incompleta devuelve indisponibilidad de documentos. La aplicación puede volver a pedir el estado tras la publicación. Los permisos de funciones concretas usan sus ámbitos contextuales y su política existente.

## Pruebas

Las pruebas unitarias cubren español, inglés, versiones pendientes, publicación y reintento. La prueba PostgreSQL usa una base desechable y verifica la serialización, los metadatos y el evento de auditoría. Configurar una conexión de pruebas en loopback con el procedimiento descrito en [Consentimientos legales](README.md).
