# NexoMarket 5.12.0 — corrección final urgente

## Render
- Se corrigió el error de sintaxis de `CentralServerService.cs` que producía la cascada de errores `CS1002/CS1012/CS1003` en la línea de la vista Storefront.
- Se eliminó el uso de atributos HTML con comillas dobles sin escapar dentro de cadenas C#.
- `Dockerfile` utiliza SDK .NET 8 y un único `dotnet publish -c Release --no-restore`.
- Se agregó alias `/api/admin/account/commission/set` para instalaciones que todavía tengan la ruta anterior en caché.

## SuperAdmin
- Comisión por cuenta vendedor desde 0%.
- Cálculo: ventas del mes × porcentaje.
- Pago mensual visible para vendedor.
- Destacar tienda.
- Súper destacar tienda.
- Color neón configurable por el vendedor; verde fluorescente por defecto.
- Plazo de vencimiento y días de tolerancia configurables por tienda.
- Al vencer: la tienda no se elimina; queda bloqueada para el vendedor.

## Web Seller
- Botón `COMPARTIR MI TIENDA / CATÁLOGO`.
- Comisión y vencimiento visibles.
- Bloqueo operativo cuando vence el pago.
- Productos y pedidos continúan centralizados.

## Windows
- Indicador de pago de plataforma.
- Muestra importe y vencimiento.
- Si Central marca la cuenta como bloqueada, se deshabilita la navegación operativa hasta que el pago o una ampliación quite el bloqueo.
- Se mantiene la sincronización central de productos, stock, pedidos y estados.

## Pedidos
- `CentralOrderId` es la identidad única del pedido.
- El endpoint de pendientes no elimina pedidos al recibir un ACK.
- Windows vuelve a consultar pedidos recientes y snapshot de cambios.
- Los cursores tienen una ventana de solapamiento para evitar perder eventos con la misma marca temporal.
- Los detalles `itemsJson` viajan hasta Windows y Seller Center.
- Se mantienen alertas y sonido de nuevo pedido.

## Validaciones realizadas
- Balance estructural de C# en CentralServer, CentralDatabase, SuperAdmin y Windows: correcto.
- JavaScript embebido extraído y validado con `node --check`: correcto.
- Se verificó que no queden atributos HTML con comillas dobles sin escapar en `CentralServerService.cs`.
- No se ejecutó `dotnet build` local porque este entorno no tiene SDK .NET instalado; Render será quien haga la compilación final dentro de Docker.


## Corrección adicional 5.12.0 FINAL3

- Se agregó el método `PlatformFeeForStore`, que faltaba y provocaba el error de compilación CS0103 en `CentralServerService.cs(358,133)`.
- El endpoint `/api/platform-fee` ahora valida `StoreId + SyncKey`, localiza al vendedor asociado y devuelve el mismo resumen de comisión/pago usado por Seller Center.
- SuperAdmin > Cuentas ahora muestra también el **Nombre de tienda** asociado a cada cuenta vendedor.
- Se mantiene la comisión inicial en 0% y el cálculo mensual configurable por cuenta.
