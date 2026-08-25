# NexoMarket 5.0.3 — Seller Center Pro + vinculación Windows

## Objetivo
Esta versión amplía el Seller Center para acercarlo al patrón operativo de marketplaces grandes: catálogo, inventario visual, pedidos, métricas, configuración y vinculación clara entre Web y Windows.

## Cambios principales

### 1. Vinculación Web → Windows sin buscar menús
- El Seller Center muestra `VINCULAR WINDOWS` directamente en el panel.
- `/seller/devices` genera automáticamente un código temporal de un solo uso, sin volver a pedir la contraseña si la sesión Web ya está autenticada.
- El código dura 10 minutos y se puede copiar con un botón.
- El Store ID sigue siendo la identidad permanente de la tienda.
- Windows ya acepta el código mediante `VINCULAR WINDOWS`.
- Se agregó un botón en la ventana Windows para abrir directamente el Seller Center de vinculación.

### 2. Productos e inventario
- Grilla visual responsive de productos.
- 5 columnas en escritorio, reduciendo columnas en pantallas menores.
- Buscador por nombre/SKU/categoría.
- Filtro de stock bajo/sin stock.
- Subida de foto desde galería/archivos.
- Subida de video corto desde archivos.
- Vista previa local antes de guardar.
- Los archivos se almacenan en R2 y la URL servida por NexoMarket queda guardada en el producto.
- Límite actual de archivo: 8 MB por imagen/video.

### 3. Pedidos
- Vista operativa de pedidos centralizados.
- Búsqueda por pedido, cliente o correo.
- Filtro por estado.
- Actualización de estado sin salir del Seller Center.

### 4. Métricas
- Ventas acumuladas.
- Ticket medio.
- Cantidad de pedidos.
- Productos publicados.
- Stock bajo.
- Gráfico de ventas de los últimos 7 días con datos reales de pedidos.
- Distribución de estados de pedidos.

### 5. Configuración
- Nueva sección `Configuración`.
- Nombre de tienda, nombre legal, categoría, dirección, ciudad, provincia, logo, slug, descripción, delivery y retiro.
- Estos datos quedan asociados al Store ID y sobreviven a cambios de versión mientras se conserve el PostgreSQL central.

## Referencia de diseño
El diseño toma como referencia patrones públicos de Seller Hub de SHEIN, Seller Center de Temu y Central de vendedores de Mercado Libre: navegación lateral, dashboard de KPIs, gestión de productos/inventario, pedidos, marketing, finanzas, métricas y configuración. No se copian marcas, código ni interfaz propietaria; se implementan patrones funcionales equivalentes para NexoMarket.

## Despliegue
1. Mantener el mismo `NEXOMARKET_DATABASE_URL`.
2. Mantener las credenciales actuales de R2.
3. Reemplazar el proyecto en GitHub con esta versión.
4. Dejar que Render ejecute el Dockerfile y compile con .NET 8.
5. No crear un PostgreSQL nuevo.
6. Después del deploy, entrar al Seller Center y usar `VINCULAR WINDOWS`.

## Flujo de vinculación
1. Entrar a NexoMarket Web con correo y contraseña.
2. Abrir `VINCULAR WINDOWS`.
3. Copiar el código.
4. En Windows abrir `Cuenta vendedor`.
5. Pegar el código en `Código de vinculación / QR`.
6. Pulsar `VINCULAR WINDOWS`.

## Nota de compilación
El entorno de preparación de este ZIP no tiene instalado el SDK de .NET 8, por lo que la compilación final debe realizarse en Render mediante el Dockerfile incluido.
