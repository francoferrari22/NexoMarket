# NexoMarket 5.0.2 — identidad persistente + inventario visual + formulario Seller Center

## 1. Cuenta de vendedor persistente

- PostgreSQL es la fuente de verdad para la cuenta central.
- Un mismo `StoreId` de vendedor solo puede tener una cuenta canónica.
- Si una versión nueva de Windows intenta sincronizar la cuenta con otro correo, no crea otra identidad: actualiza la identidad existente del Store ID.
- El correo tampoco puede pertenecer a otra identidad diferente.
- Se agregó una migración para limpiar duplicados antiguos por `StoreId` antes de crear el índice único.
- El registro web rechaza crear una segunda cuenta para un Store ID que ya tiene vendedor.
- Cambiar de versión o reiniciar Render no elimina la cuenta mientras `NEXOMARKET_DATABASE_URL` siga apuntando al mismo PostgreSQL.

## 2. Seller Center — Productos e inventario

- El inventario pasó de tabla a tarjetas visuales con foto cuadrada.
- Diseño de 5 columnas en escritorio y adaptación automática en pantallas pequeñas.
- La foto usa `WebImageUrl` y se muestra como `object-fit: cover` dentro de un cuadrado.
- Si falta la foto, aparece un indicador `SIN FOTO` en lugar de una imagen rota.

## 3. Nuevo producto — formulario que se refrescaba solo

- Se eliminó la recarga automática cada 2,5 segundos de la vista de productos.
- También se protegió la actualización en vivo del Seller Center: si el usuario está escribiendo en el formulario de nuevo producto o edición, una sincronización externa no recarga la página y no borra lo escrito.
- La actualización automática solo vuelve a refrescar cuando no hay cambios sin guardar.

## Despliegue

1. Reemplazar el proyecto actual por este ZIP.
2. Subir a GitHub.
3. Hacer deploy/redeploy en Render.
4. Verificar que `NEXOMARKET_DATABASE_URL` siga apuntando al PostgreSQL existente. **No crear un PostgreSQL nuevo.**
5. No modificar ni borrar las variables de R2 existentes.

La compilación final debe hacerla Render porque el proyecto requiere el SDK .NET 8.
