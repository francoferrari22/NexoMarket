# NexoMarket 5.12.13

## Corrección crítica: vinculación de pedidos comprador → vendedor

Esta versión conserva el paquete completo de NexoMarket y elimina únicamente el panel Windows de administración. El SuperAdmin permanece.

### Cambio principal
Los pedidos nuevos guardan explícitamente:
- `StoreId`
- `SellerAccountId`
- `SellerEmail`

El Seller Center Web considera un pedido perteneciente a su cuenta si coincide por cualquiera de esas identidades. Esto evita que un StoreId histórico/desincronizado deje un pedido invisible.

### Seller Center
`/api/seller/live` ahora detecta pedidos usando la misma vinculación y conserva:
- alerta visual
- sonido
- parpadeo de 10 ciclos
- notificación del navegador cuando está permitida
- actualización de la pantalla

### Compatibilidad
Los pedidos anteriores siguen siendo visibles por `StoreId`.

### Versión
`5.12.13`
