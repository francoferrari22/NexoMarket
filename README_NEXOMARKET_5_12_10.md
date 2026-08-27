# NexoMarket 5.12.10 — paquete completo, sin panel Windows

Esta entrega conserva la base completa de NexoMarket y elimina únicamente el proyecto del panel administrativo Windows (`NexoMarket.Admin`).

## Se conserva
- Central Server / Marketplace Web.
- Seller Center Web.
- Flujo de pedidos comprador -> tienda.
- Resolución de tienda por evidencia de productos.
- Subida de comprobantes.
- Persistencia central PostgreSQL/R2 configurada por Render.
- Alertas del Seller Center, sonido, parpadeo y seguimiento.
- NexoMarket SuperAdmin completo.
- Herramientas existentes.
- Instalador/herramientas existentes que no forman parte del proyecto `NexoMarket.Admin`.
- Documentación y archivos auxiliares del proyecto.

## Se elimina
- Únicamente `NexoMarket.Admin/` (panel Windows).
- Únicamente `NexoMarket.Admin.sln`.

El Android Companion que estaba dentro del antiguo proyecto Windows no se presenta como aplicación operativa en esta entrega; la aplicación Android se desarrollará posteriormente como proyecto independiente.

## Backend
La parte central utiliza la revisión de pedidos/comprobantes de 5.12.9 y se identifica como 5.12.10 en `/api/version`.
