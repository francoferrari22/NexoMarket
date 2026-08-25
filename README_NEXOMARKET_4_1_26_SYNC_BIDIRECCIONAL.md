# NexoMarket 4.1.26 — Catálogo central bidireccional

Esta versión corrige el problema estructural por el que NexoMarket Windows y el Seller Center web podían mostrar dos catálogos diferentes.

## Fuente única

El `StoreId` identifica una única tienda y es también el código de emparejamiento. El catálogo central de Render/R2 es la fuente común:

`Windows <-> Render Central/R2 <-> Seller Center Web`

## Productos e inventario

- Crear producto en Windows -> Central -> Seller Center.
- Editar precio, oferta, stock, mínimo, SKU, marca, talle, color, descripción, publicación o estado en Windows -> Central -> Web.
- Crear producto desde Seller Center -> Central -> Windows.
- Editar producto/stock desde Seller Center -> Central -> Windows.
- Eliminar producto en cualquiera de los dos lados -> eliminación lógica central + eliminación local en la otra aplicación.
- Se utiliza `UpdatedAt` para evitar que una copia vieja sobrescriba una modificación más nueva.
- Los productos creados desde la web reciben IDs centrales altos para evitar colisiones con IDs locales antiguos de Windows.
- Las imágenes web pueden usar una URL pública; las imágenes locales de Windows se pueden publicar a R2 cuando R2 está configurado.

## Actualización visual

- Windows recibe cambios centrales y refresca automáticamente las páginas de Productos, Inventario, Pedidos, Delivery, Ventas, Clientes, Promociones, Estadísticas e Inicio.
- Seller Center refresca el catálogo aproximadamente cada 2,5 segundos cuando está en Productos e inventario.
- El escaparate público refresca el catálogo periódicamente.

## Cuentas

La cuenta web continúa utilizando correo/contraseña. Windows se vincula por `StoreId`. La cuenta vendedor queda asociada al mismo StoreId y Central evita crear una segunda identidad de vendedor para la misma tienda.

## Persistencia en Render

R2 es necesario para que tiendas, cuentas, catálogo y pedidos sobrevivan a reinicios/recreaciones del contenedor de Render. Configurar en Render:

- `R2_ACCOUNT_ID`
- `R2_ACCESS_KEY_ID`
- `R2_SECRET_ACCESS_KEY`
- `R2_BUCKET`
- `R2_PUBLIC_BASE_URL`

## URL central

Windows lee `NexoMarketCentral.url` junto al ejecutable. Si se cambia el servicio de Render, se puede cambiar ese archivo sin recompilar Windows.

## Emparejamiento

Windows conserva su Store ID y puede seguir funcionando localmente. Cuando el mismo Store ID se registra en la Web, Central lo trata como la misma tienda; no crea un segundo catálogo. El Store ID se normaliza y se utiliza para derivar la clave interna de sincronización, por lo que no quedan claves aleatorias diferentes entre Windows y Web.
