# NexoMarket 5.11.1

## Sincronización
La versión agrega una reconciliación completa de pedidos además del delta normal. Windows consulta `/api/orders/snapshot` con la SyncKey derivada del StoreId y realiza upsert por `CentralOrderId`. Los estados modificados desde Windows se publican en Central; los cambios hechos desde Web vuelven a Windows.

También se mantiene la sincronización de productos y stock existente.

## Alertas
- Windows reproduce una alerta sonora cuando entra un pedido nuevo.
- Seller Center Web reproduce un aviso sonoro cuando aumenta la cantidad de pedidos pendientes.

## Tienda Online
En Seller Center, `Tienda Online` queda visible al principio del menú y permite:
- Nombre público
- Nombre del sistema/marca
- Nombre legal
- Categoría
- Dirección
- Ciudad/provincia
- Coordenadas GPS con geocodificación
- Logo por carga de archivo
- Foto del local/portada por carga de archivo
- Delivery y retiro
- Horario automático

## Horario automático
Con `AutoSchedule=1`, el servidor calcula la hora de Argentina y abre/cierra la tienda entre `OpenTime` y `CloseTime`. Una tienda cerrada sigue pudiendo sincronizar Windows y ser administrada.

## Conflicto de nombre
Si Windows tiene un cambio local pendiente, el ciclo de sincronización no pisa ese cambio con un perfil central antiguo. Después de publicarlo, Central pasa a ser la referencia común.

## Marketplace inicial
Las tarjetas de tiendas incorporan ubicación, descripción, delivery/retiro, promociones pequeñas y foto del local cuando existe.

## Android
La ficha pública de productos usa 5 columnas en pantallas móviles para mostrar más artículos simultáneamente.

## Super Admin
La herramienta separada sigue siendo HTA + BAT y no requiere MSBuild, Visual Studio, .NET ni clave maestra.


## 5.11.1
- Android Seller Center product images now use the native image/file chooser without capture=environment, so the camera is not forced.
- New order alert: red foreground banner for 10 seconds + sound in Seller Center web and Windows administrator.
- Super Admin 5.11.1 separate HTA includes account filters: all, sellers, buyers.
