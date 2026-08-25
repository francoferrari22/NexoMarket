NexoMarket 4.1.17 — Seller Center + cuentas, sin sistema de licencias

CAMBIOS
- Se eliminó el bloqueo de licencia del arranque de NexoMarket Admin.
- Se eliminó la interfaz de licencia/código/token del Seller Center central.
- Se eliminaron los componentes del License Manager del repositorio y de la solución.
- La creación de cuenta de vendedor desde Windows valida y guarda correctamente la cuenta.
- Las cuentas antiguas sin StoreId se completan con el StoreId de la instalación.
- Al crear o iniciar una cuenta, se intenta publicar inmediatamente en el servidor central.
- Se mantiene la sincronización periódica de cuentas, productos, promociones, tiendas y pedidos.
- Se mantiene el Seller Center completo restaurado y el Buyer Center con tiendas y pedidos.
- Se mantiene AnyCPU y .NET Framework 4.8 para NexoMarket Admin.

IMPORTAR A GITHUB
1. Reemplazar el contenido del repositorio por el contenido de este ZIP.
2. Hacer commit/push a la rama que usa Render.
3. En Render, hacer Manual Deploy del último commit.
4. Probar primero /health y luego el registro de vendedor.

NOTA
Este corte deja las licencias completamente fuera del flujo de NexoMarket. Se conserva la identidad de cuenta (correo, contraseña, StoreId) para sincronización y acceso al Seller Center.
