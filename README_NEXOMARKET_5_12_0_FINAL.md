# NexoMarket 5.12.0 — paquete limpio

## Cambios de esta entrega

- Buscador de tiendas por nombre con resultados mientras se escribe.
- Enlaces públicos de catálogo con slug basado en el nombre de la tienda (`/store/nombre-de-tienda`).
- Botón flotante **NEXO MARKET**: aparece al desplazarse hacia arriba y vuelve al inicio sin cerrar la cuenta.
- El logotipo NexoMarket del encabezado vuelve al inicio.
- Compartir tienda/catálogo desde Seller Center comparte el nombre de la tienda y el enlace directo a su catálogo.
- SuperAdmin: al configurar comisión ya no depende de tener una fila seleccionada; permite elegir una cuenta vendedora del listado.
- Se conserva la columna **Tienda** junto con ID, nombre, correo, rol, Store ID, comisión y monto mensual.
- Mantiene las funciones anteriores de pedidos, cupones, reputación, destacadas, sincronización Web/Windows y pagos de plataforma.

## Compilación

### Render
Usar el `Dockerfile` del directorio raíz y dejar que Render ejecute el `dotnet publish` dentro del contenedor.

### SuperAdmin
Entrar en `NexoMarket.SuperAdmin` y ejecutar `COMPILAR_FACIL_SUPERADMIN.bat` en una PC con .NET SDK instalado.

## Nota
No se deben eliminar archivos funcionales del proyecto solo para reducir el tamaño. La limpieza de esta entrega elimina documentación/versiones duplicadas, pero conserva los proyectos necesarios para Central, Windows/Admin, Android Companion, SuperAdmin e instalador.


Corrección aplicada: 5.12.7 — resolución de pedidos por evidencia de productos.
