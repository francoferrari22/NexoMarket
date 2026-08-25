# NexoMarket

## Estructura
- `NexoMarket.Admin/` — aplicación Windows Forms del vendedor, compatible con .NET Framework 4.8 y AnyCPU.
- `NexoMarket.CentralServer/` — servidor central para tiendas, cuentas, catálogo, pedidos y Seller/Buyer Center.
- `Installer/` — instalador del administrador.

## Flujo actual
- El vendedor crea/inicia su cuenta desde NexoMarket Windows.
- La cuenta queda vinculada al `StoreId` de la instalación.
- La cuenta se publica inmediatamente en el servidor central y además se sincroniza periódicamente.
- El Seller Center central muestra el dashboard completo.
- El Buyer Center muestra las tiendas disponibles, catálogo y pedidos.
- El inventario y la operación local siguen en NexoMarket Windows.

## Render
El servidor central puede desplegarse en Render. El endpoint `/health` devuelve `NexoMarket Central OK`.
