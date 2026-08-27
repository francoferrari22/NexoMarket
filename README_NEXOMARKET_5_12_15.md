# NexoMarket 5.12.15 — identidad seller y buzón central de pedidos

Esta versión NO repite el enfoque 5.12.14. Corrige una causa distinta detectada en el código: una cuenta seller de PostgreSQL podía ser sobrescrita/reconciliada con un StoreId de XML legado. Eso podía dejar al vendedor autenticado apuntando a una tienda distinta y mostrar el buzón vacío aunque el pedido estuviera creado.

## Cambios de raíz
- PostgreSQL es la identidad canónica de la cuenta Web cuando está habilitado.
- Se elimina la reconciliación automática que cambiaba el StoreId de PostgreSQL por el StoreId XML legado.
- `FindSellerByStore` consulta PostgreSQL primero; XML queda como compatibilidad cuando no existe DB.
- `ResolveSellerCanonicalStore` prioriza el StoreId de la cuenta central.
- El Seller Center agrega un segundo canal de lectura: el documento central `orders` de PostgreSQL. Así no depende exclusivamente del índice `nexomarket_orders`.
- El buzón sigue deduplicando por `CentralOrderId`.
- Se mantienen comprador, carrito, comprobante, pedidos, Seller Center, alertas, sonido, parpadeo y SuperAdmin.
- El antiguo panel Windows no se incluye.
- El dashboard continúa actualizándose silenciosamente mediante polling.

## Importante
La persistencia Web→Web en Render requiere `NEXOMARKET_DATABASE_URL` configurada. Si no existe, el servidor solo puede utilizar almacenamiento local y no hay garantía de intercambio entre instancias.

## Verificación
`GET /api/version` debe devolver `5.12.15`.
