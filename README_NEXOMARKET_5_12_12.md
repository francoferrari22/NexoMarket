# NexoMarket 5.12.13

## Corrección estructural del circuito comprador -> vendedor

Esta versión rehace el punto de entrada del checkout web: `/api/cart/checkout`.
El carrito identifica la tienda mediante los productos reales del carrito, resuelve la cuenta seller canónica y recién entonces crea el pedido central.

Los pedidos se replican en una tabla PostgreSQL dedicada (`nexomarket_orders`) además del documento XML legado. El Seller Center consulta esa tabla para detectar y mostrar pedidos entre instancias de Render.

Se mantiene `/api/orders/create` como alias de compatibilidad.

Se conserva el SuperAdmin completo. Se elimina únicamente el panel/herramientas Windows heredadas y el instalador del panel Windows.

Versión: 5.12.13
