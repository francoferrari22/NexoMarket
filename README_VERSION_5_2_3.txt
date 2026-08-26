NexoMarket 5.2.3
Cambios: sesión persistente, cierre de tienda sin logout, directorio muestra tiendas cerradas, identidad tienda en Web/Windows, caché de errores de media corregida.
NO modificar credenciales de Render/PostgreSQL/R2.

--- CORRECCIÓN FINAL 25/08/2026 ---
- Marketplace principal renovado con tarjetas glass/translúcidas, neones verdes/azules/violetas, curvas de fondo y profundidad visual alineada con Seller Center.
- Seller Center mantiene la sesión del vendedor sin expulsarlo al actualizar pedidos; el cambio de estado se realiza por AJAX sobre la misma pestaña.
- El estado de un pedido ya no redirige a /login ni a la portada cuando se actualiza desde Pedidos.
- Se reforzó la sesión persistente del Web local: después de reinicios del servidor/watchdog la cuenta puede reconstruirse desde la cuenta guardada, evitando expulsiones innecesarias.
- Carga de imágenes de productos reforzada: subida central, vista previa, carga de foto al editar productos y visualización inmediata en inventario/marketplace.
- No se modificaron las URLs ni la lógica de conexión central, sincronización, R2 o base de datos.
