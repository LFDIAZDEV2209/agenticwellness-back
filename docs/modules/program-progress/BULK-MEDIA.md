# Lotes de biblioteca — 1.13

CodeGraph y `MediaController` confirman las operaciones existentes: crear/editar
un medio, `upload-intent`, publicar/despublicar/archivar, eliminar, referencias,
reordenar y limpieza. Se reutilizan la subida directa y las validaciones de
`CreateMediaItemCommand` y `PublishMediaItemCommand`; no se duplican binarios ni
se añade otro mecanismo de subida.

- `POST /api/v1/media/bulk/drafts` (`Media.Create`): `{files:[{nombre,
  storageKey,contentType?,fileSizeBytes?,durationSecs?,title?,author?,category?,
  thumbnailKey?}]}`. Solo podcasts, siempre borradores. Sin título se toma el
  nombre del archivo sin extensión. El actor procede de la sesión.
- `POST /api/v1/media/bulk/publish` (`Media.Publish`):
  `{files:[{nombre,mediaId}]}`. Usa la validación física de publicación existente.

Ambos admiten de 1 a 100 archivos y responden HTTP 200 con una lista, en el mismo
orden: `[{nombre,estado:"borrador"|"publicado"|"error",mediaId?,error?}]`.
Un fallo no revierte los correctos. Se reenvían únicamente las entradas fallidas;
también es seguro reenviar el lote completo: storageKey identifica el borrador
existente, y un medio ya publicado no vuelve a publicarse ni se baja a borrador.
Los archivados se informan como error y se gestionan mediante el editor.

Cada archivo usa un scope de persistencia independiente para impedir que un
INSERT fallido contamine los siguientes. No hay una transacción del lote ni
consultas por paciente. Cancelar interrumpe los pendientes y conserva las filas
ya confirmadas; pueden recuperarse reenviando las claves originales.
La prueba PostgreSQL ejecuta cinco archivos con uno inválido, reintenta el quinto,
publica cuatro con un fallo de archivo, recupera el quinto y demuestra que al
repetir el lote no se reescriben ni se vuelven a verificar los correctos.
