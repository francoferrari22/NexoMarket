# NexoMarket 5.0.0 — Central PostgreSQL

Esta versión establece la nueva arquitectura central: PostgreSQL es la fuente de verdad para Web y Windows; XML/R2 quedan como respaldo y migración.

Ver `README_NEXOMARKET_5_0_0_CENTRAL_POSTGRES.md` para el despliegue.

NexoMarket 4.1.32 — sincronización central inteligente cada 20 segundos.

# NexoMarket 4.1.30 — Seller Web Store ID + Render Build Fix

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


## 4.1.30
- Corrige el error de sintaxis C# en `CentralServerService.cs` que impedía compilar Render (CS1002/CS1513 alrededor de Storefront).
- Simplifica la generación de los atributos `onclick` del catálogo usando comillas simples HTML para evitar colisiones con strings C#.
- Mantiene el acceso Seller Center por Store ID y el resto de la arquitectura de la versión 4.1.29.

## 5.1.2 — Render build fix
Se corrigió el error de compilación en CentralServerService.cs causado por HTML fuera de una cadena C#. El Dockerfile imprime hashes de fuentes para verificar que Render compile el contenido correcto.
