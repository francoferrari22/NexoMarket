NexoMarket 5.12.13
CORRECCION DEFINITIVA DEL FLUJO WEB DE PEDIDOS

Esta versión corrige el bloqueo que devolvía ERROR|seller_account_not_found al confirmar un pedido.

Regla de enrutamiento:
- StoreId es la identidad canónica obligatoria del pedido.
- SellerAccountId y SellerEmail son metadatos de vinculación cuando existe la cuenta vendedora.
- La ausencia/desincronización de la cuenta vendedora NO puede impedir una compra válida.
- Seller Center recibe y filtra los pedidos por StoreId y, adicionalmente, por identidad de vendedor.

Persistencia:
- Si PostgreSQL está habilitado, CreateOrder verifica que el pedido haya quedado persistido en nexomarket_orders antes de devolver OK al comprador.
- Si la persistencia central falla, el comprador recibe un error real en vez de un falso “Pedido enviado”.

Esta versión conserva el paquete completo, incluido NexoMarket.SuperAdmin, y elimina únicamente el panel Windows antiguo.
