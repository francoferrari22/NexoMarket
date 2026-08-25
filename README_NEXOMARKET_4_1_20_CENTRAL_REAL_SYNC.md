# NexoMarket 4.1.20 — Central Real Sync

## Objetivo
Esta versión corrige la separación entre la web local de Windows y la instancia pública de Render.

### Flujo obligatorio
Windows NexoMarket -> HTTPS -> NexoMarket Central en Render -> R2 -> Marketplace/Seller Center.

El navegador público nunca lee los XML locales de la PC del vendedor.

## Correcciones
- Fuerza TLS 1.2 en las comunicaciones HTTPS desde el administrador Windows.
- Migra automáticamente URLs antiguas de localhost/LAN al endpoint central configurado por el proyecto.
- La URL pública de una tienda local ya no se publica como localhost/LAN; se publica como `https://nexomarket-central.onrender.com/store/{StoreId}` salvo que exista una URL pública externa válida.
- La tienda se publica activa por defecto.
- Registrar tienda devuelve errores reales y valida el `SyncKey` existente para evitar dos identidades para el mismo StoreId.
- Productos y promociones publicados desde Windows llevan `StoreId + SyncKey` y el servidor valida la pertenencia de la tienda.
- Render restaura desde Cloudflare R2 los archivos centrales de cuentas, catálogo y pedidos en cada arranque, incluso si el contenedor ya contiene archivos antiguos.
- La creación/edición/eliminación de productos desde Windows dispara una sincronización inmediata además del ciclo periódico.
- Las cuentas siguen utilizando una identidad central por correo y StoreId.
- No se reintroduce ningún sistema de licencias.

## Render
Configurar en Render:
- `PUBLIC_BASE_URL=https://nexomarket-central.onrender.com`
- `R2_ACCOUNT_ID`
- `R2_ACCESS_KEY_ID`
- `R2_SECRET_ACCESS_KEY`
- `R2_BUCKET=nexomarket`
- `R2_PUBLIC_BASE_URL` si se quieren publicar imágenes mediante URL pública R2.

## Prueba de aceptación
1. Abrir Windows y guardar la configuración de tienda con "TIENDA ACTIVA EN LA WEB PRINCIPAL".
2. Comprobar que aparece la tienda en `https://nexomarket-central.onrender.com/`.
3. Crear un producto publicado online en Windows y actualizar el marketplace público.
4. Crear una cuenta de vendedor en Windows y entrar en Render con el mismo correo/contraseña.
5. Crear una cuenta desde Render para una tienda existente y autenticarla desde Windows.
6. Reiniciar el servicio de Render y comprobar que las tiendas, cuentas, productos y pedidos siguen presentes.
