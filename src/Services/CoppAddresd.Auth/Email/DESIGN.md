---
name: Copp Adresd transactional email
description: Bienvenida legible y coherente con la identidad existente del ERP.
colors:
  primary: "#142855"
  action: "#035d4d"
  brand: "#03466a"
  surface: "#ffffff"
  secondary-surface: "#edf5f3"
  body: "#52677b"
  border: "#dfe9e8"
rounded:
  button: "8px"
  message: "16px"
---

# Design System: Copp Adresd email

## Overview

Extensión de la marca existente al correo transaccional. La prioridad es reconocer
al remitente y completar el acceso con instrucciones claras. Alcance exclusivo
del email, sin redefinir el diseño del ERP o la app.

## Colors

Azul `#142855` para encabezado y títulos; verde `#035d4d` para acciones con texto
blanco. Cuerpo blanco y fondo secundario verde claro. El texto de apoyo usa
`#52677b`; divisores `#dfe9e8`. Sin degradados ni sombras decorativas.

## Typography

Título 36px (30px móvil), secciones 21px, cuerpo 15–16px y pie 11–14px. El stack
Arial/Helvetica es una excepción deliberada por compatibilidad entre clientes
de correo; no se adopta como tipografía del resto del producto. Sin webfonts.

## Layout

Tabla fluida con máximo de 600px. Padding horizontal 40px en escritorio, 22px
en pantallas de hasta 600px. Orden: marca, saludo, rol, primer acceso si aplica,
ERP si está habilitado, app si está habilitada, ayuda y seguridad.

## Elevation & Depth

Separación por color y espacio; no hay elevación con sombras.

## Shapes

Contenedor de 16px y botones de 8px. Los separadores de capacidades son líneas
horizontales de 1px; las tiendas pendientes son bloques informativos sin enlace.

## Components

Logo PNG original (`coppaddresd-front/public/LogoIndividual.png`), copiado con
metadatos de procedencia y embebido mediante CID con alt. No depende de un CDN.
Botones en tablas y estilos inline. La alternativa de texto contiene las mismas
instrucciones y destinos. El generador de muestras usa el mismo renderer que producción.

## Do's and Don'ts

Mostrar solo accesos guardados y acciones autorizadas. Escapar nombre, roles y
etiquetas. No incluir contraseñas, enlaces ficticios, módulos bloqueados ni
iconos que dependan de fuentes. Las URLs de tiendas vacías muestran Próximamente.
