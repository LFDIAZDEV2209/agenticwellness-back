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
  button: "28px"
  message: "22px"
---

# Design System: Copp Adresd email

## Overview

Extensión de la marca existente al correo transaccional. La prioridad es reconocer
al remitente y completar el acceso con instrucciones claras. Alcance exclusivo
del email, sin redefinir el diseño del ERP o la app.

## Colors

Azul `#142855` para encabezado y títulos; verde `#035d4d` para acciones con texto
blanco. Cuerpo blanco y fondo secundario verde claro. El texto de apoyo usa
`#52677b`; divisores `#dfe9e8`. El encabezado combina azul y verde en un
degradado de 125 grados; botones con transición verde a azul. Ambos conservan
color sólido de respaldo para clientes sin soporte de degradados.

## Typography

Saludo editorial Georgia de 54px (44px móvil), secciones sans de 23px, cuerpo
15–16px y pie 11–13px. Georgia también aparece en el cierre de ayuda, a 25px.
Arial/Helvetica se conserva en cuerpo y controles por compatibilidad entre
clientes de correo; no redefine la tipografía del resto del producto. Sin webfonts.

## Layout

Tabla fluida con máximo de 600px. Padding horizontal de 42px en escritorio,
24px de cuerpo y 26px de encabezado en pantallas de hasta 600px. Orden: marca
integrada al encabezado, saludo, rol, primer acceso si aplica,
ERP si está habilitado, app si está habilitada, ayuda y seguridad.

## Elevation & Depth

Separación por color y espacio; no hay elevación con sombras.

## Shapes

Contenedor de 22px, sello del logo de 18px y botones de 28px. La activación
aparece en una superficie verde clara de 16px; las capacidades usan filas
sombreadas con separación de 8px. Las tiendas pendientes tienen borde
discontinuo de 1px y no se presentan como enlaces.

## Components

Logo PNG original (`coppaddresd-front/public/LogoIndividual.png`), copiado con
metadatos de procedencia y embebido mediante CID con alt. No depende de un CDN.
Logo sobre un sello blanco y cierre de ayuda sobre azul oscuro. Botones en
tablas y estilos inline. La alternativa de texto contiene las mismas
instrucciones y destinos. El generador de muestras usa el mismo renderer que producción.

## Do's and Don'ts

Mostrar solo accesos guardados y acciones autorizadas. Escapar nombre, roles y
etiquetas. No incluir contraseñas, enlaces ficticios, módulos bloqueados ni
iconos que dependan de fuentes. Las URLs de tiendas vacías muestran Próximamente.
