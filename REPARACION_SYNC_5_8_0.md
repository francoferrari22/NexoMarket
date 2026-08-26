# NexoMarket 5.8.0 — Reparación de sincronización

## Problemas corregidos

- `ERROR|sync_key` al actualizar estados de pedidos.
- Windows no recibía pedidos pendientes cuando existía una clave de sincronización antigua o faltante.
- Un pedido recibido por Windows descontaba el stock local, pero no publicaba inmediatamente ese stock de vuelta al Central.
- Los cambios de estado realizados en Web/Seller Center no se reflejaban en Windows.

## Solución

La SyncKey ahora se deriva determinísticamente del `StoreId` y el servidor repara automáticamente claves antiguas/faltantes. Windows también repara su clave al sincronizar.

El ciclo de pedidos queda:

Web → Central → Windows → descuento local → publicación de stock → Central.

Los estados quedan:

Web/Windows → Central → delta de estados → Windows.

No se modifican ni eliminan tiendas, cuentas ni productos existentes.
