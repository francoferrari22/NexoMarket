# NexoMarket 4.1.25 — Store ID Pairing

Windows y Web usan el mismo Store ID como identidad de la tienda. Windows puede crear la cuenta local y continuar sin esperar a Render; la cuenta Web se vincula posteriormente pegando el mismo Store ID. Central mantiene una sola tienda y una sola identidad vendedora canónica por Store ID.

---

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
