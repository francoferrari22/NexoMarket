# NexoMarket 4.1.18 — sincronización bidireccional de cuentas y tiendas

## Objetivo

Esta versión corrige el problema por el cual una cuenta creada en Windows no aparecía en la web y una cuenta creada en la web no aparecía en Windows.

La identidad de una cuenta de vendedor es:

- correo electrónico normalizado
- StoreId de la tienda
- credenciales (salt + password hash)

NexoMarket Central mantiene el registro central y Windows sincroniza las cuentas de su StoreId.

## Flujo

Windows → publica tienda → publica cuentas → Central.

Central/Web → crea o actualiza cuenta → Windows la descarga automáticamente por StoreId.

Además, al abrir el formulario de cuenta de vendedor o intentar iniciar sesión en Windows, se fuerza una sincronización inmediata para evitar el mensaje falso de "cuenta inexistente".

## Tiendas

- El directorio central muestra TODAS las tiendas activas.
- La ubicación solamente ordena las tiendas por cercanía; no elimina las tiendas lejanas.
- Guardar la configuración web con la sincronización habilitada publica la tienda inmediatamente y la marca como activa.
- Cada tienda conserva su StoreId.

## Seguridad de sincronización

La descarga de cuentas usa una clave privada por tienda (`central_sync_key`). El servidor no entrega salt/password hashes sin esa clave.

## Persistencia de Render

Para que las tiendas, cuentas, catálogo y pedidos sobrevivan a reinicios de Render, configurar las variables de Cloudflare R2 indicadas en `render.yaml`:

- R2_ACCOUNT_ID
- R2_ACCESS_KEY_ID
- R2_SECRET_ACCESS_KEY
- R2_BUCKET
- R2_PUBLIC_BASE_URL (si se requiere publicar archivos)

## GitHub / Render

1. Reemplazar el contenido del repositorio por este ZIP.
2. Commit y push a la rama conectada a Render.
3. Hacer Manual Deploy del último commit.
4. Comprobar `/health`.
5. Crear una cuenta vendedor en Windows y comprobar que aparece en el Seller Center.
6. Crear otra cuenta vendedor desde la web, elegir la tienda y comprobar que aparece en Windows.

Las licencias continúan fuera de esta versión, tal como se solicitó.
