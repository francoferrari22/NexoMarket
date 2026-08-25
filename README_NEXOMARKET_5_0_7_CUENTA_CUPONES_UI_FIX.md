# NexoMarket 5.0.7 — Cuenta existente + cupones + iconos Seller Center

## Windows · Cuenta central
- Se agregó `YA TENGO CUENTA · INICIAR SESIÓN`.
- El vendedor puede ingresar directamente con correo + contraseña.
- No se vuelve a pedir Store ID: se obtiene de la cuenta central.
- Si la cuenta es de vendedor, Windows guarda automáticamente el mismo Store ID y habilita la sincronización/Seller Center.

## Seller Center Web
- `/seller-login` ahora permite entrar como vendedor solamente con correo y contraseña.
- El Store ID se toma de la cuenta autenticada.
- Se reemplazaron los emojis problemáticos de navegación/acciones por iconos SVG inline, evitando cuadrados en navegadores/fuentes que no soportan emojis.
- Se corrigieron especialmente los accesos de Pedidos / Atender pedidos y Productos.

## Cupones
- Windows recupera la pantalla `Cupones` en la navegación.
- Permite crear cupones con:
  - porcentaje o importe fijo;
  - límite máximo de usos;
  - estado activo;
  - vigencia de 30 días por defecto.
- Los cupones se publican y recuperan desde el Central Server usando el mismo Store ID y sync key.
- Seller Center Web incluye generador y listado de cupones.
- La tienda pública muestra cupones vigentes y permite cargarlos al carrito.
- Al confirmar un pedido, el servidor valida vigencia/límite y registra el uso del cupón.

## Compatibilidad
- Se conserva el Store ID existente.
- No se crea una base PostgreSQL nueva.
- Se mantiene el esquema de sincronización actual y la persistencia del catálogo central.
