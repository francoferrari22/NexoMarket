# NexoMarket 4.1.27 — CENTRAL UNIFIED LIVE

Windows y Seller Center Web operan sobre la misma tienda central identificada por StoreId.

- Windows sincroniza cada ~1.8 s.
- Seller Center consulta cambios centrales cada ~1.8 s.
- La tienda pública consulta el catálogo central cada ~1.8 s sin perder el carrito.
- Productos creados/editados en Windows se publican al central y aparecen en Web.
- Productos creados/editados en Web se guardan en Central y Windows los recupera.
- El carrito del comprador es un panel flotante y ya no ocupa permanentemente la primera pantalla.
- Directorio de tiendas en 3 columnas con logo/foto cuando existe.
- Se agregan acentos violetas al marketplace/Seller Center.

IMPORTANTE: Render debe desplegar este commit y NexoMarketCentral.url debe apuntar al servicio central real.
