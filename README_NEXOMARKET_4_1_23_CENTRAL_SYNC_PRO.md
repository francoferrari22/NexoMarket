# NexoMarket 4.1.23 — CENTRAL SYNC PRO

## Corrección principal

Esta versión corrige un fallo crítico de persistencia en Render: al reiniciar el contenedor, el servidor podía crear un registro de tiendas vacío y subirlo a R2 antes de restaurar los datos persistentes. Eso podía hacer que el mismo Store ID funcionara durante una sesión y después Windows recibiera `store_not_found`.

### Persistencia
- R2 se restaura **antes** de cargar cualquier documento.
- Tiendas, cuentas, catálogo y pedidos se recuperan al arrancar.
- Nunca se reemplaza una copia válida de R2 por un documento vacío durante el arranque.
- Los archivos nuevos sí se inicializan y se guardan en R2.

### Store ID
- Windows se identifica exclusivamente por Store ID.
- Se normalizan espacios y mayúsculas/minúsculas.
- `StoreConnect` ya no exige que exista una cuenta de vendedor para conectar una tienda activa; la cuenta es sincronizada después.
- Se mantienen `StoreId -> SyncKey -> Store` como identidad central.

### Diagnóstico
- `/health` comprueba que Render esté vivo.
- `/api/sync/diagnostics?storeId=...` permite verificar tienda, cuentas, productos, R2 y SyncKey.
- Windows distingue entre servidor inaccesible, Store ID inexistente y tienda desactivada.

### Sincronización
- Windows ejecuta sincronización cada 5 segundos después del arranque inicial.
- Publica tienda, cuentas, productos y promociones.
- Recupera cuentas y catálogo desde Central.
- Los cambios web se pueden descargar en el siguiente ciclo de sincronización.

### Licencias
El sistema de licencias permanece eliminado.

### Compatibilidad
El administrador Windows conserva .NET Framework 4.8 y AnyCPU. El servidor central continúa en .NET 8 para Render.

## Variables de Render

Configurar correctamente:
- `PUBLIC_BASE_URL`
- `R2_ACCOUNT_ID`
- `R2_ACCESS_KEY_ID`
- `R2_SECRET_ACCESS_KEY`
- `R2_BUCKET`
- `R2_PUBLIC_BASE_URL` (si se usan URLs públicas de imágenes)

## Prueba definitiva

1. Crear vendedor/tienda en NexoMarket Web.
2. Copiar el Store ID.
3. En Windows ingresar únicamente ese Store ID.
4. Debe conectar aunque todavía no haya cuenta local.
5. Windows debe descargar la cuenta y catálogo de Central.
6. Crear/modificar un producto en Windows y comprobar que aparezca en Web.
7. Crear/modificar datos disponibles desde Web y comprobar que Windows los reciba en el ciclo siguiente.
