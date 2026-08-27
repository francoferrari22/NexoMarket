NexoMarket 5.19.0 — Comprador, pedidos y entregas

Correcciones principales:
- La sesión de comprador ahora se puede recuperar desde el token incluso si el proceso reinicia o cambia de instancia; no se pierde la cuenta al navegar.
- Los pedidos web realizados con sesión de comprador quedan asociados al CustomerId/email de la cuenta.
- El comprador puede volver a Mi cuenta y ver historial y seguimiento directamente en NexoMarket, sin depender del correo electrónico.
- Seguimiento con estados: Pendiente, Preparando, Listo, Enviado, En reparto y Entregado.
- Confirmación de recepción protegida por la cuenta del comprador y sólo permitida cuando el pedido está Entregado.
- Al confirmar recepción queda BuyerConfirmedAt y se registra la operación para que el vendedor pueda verla.
- Notificación visual dentro de Mi cuenta cuando cambia el estado del pedido.
- Botón destacado MIS PEDIDOS en Buyer Center.
- Reseñas vinculadas a un pedido entregado y confirmado; el comprador puede dejar la reseña desde ese pedido.
- Carrito con controles +/-, cantidad editable y eliminación de productos.
- Cupones con descuento porcentual o fijo reflejado en el carrito; el servidor continúa aplicando el descuento de forma autoritativa.
- Dirección de Delivery separada en calle, número de casa, departamento, ciudad y provincia; calle, número y ciudad son obligatorios.
- Seller Deliveries: ubicación reducida a botón VER UBICACIÓN.
- Seller Order Detail muestra la confirmación de recepción del comprador.
- No se modifican las rutas existentes del Seller Center ni la sincronización Windows/Central.
