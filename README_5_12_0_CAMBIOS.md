# NexoMarket 5.12.0

## Objetivo
Actualización conservadora sobre 5.11.2. No reemplaza el núcleo de sincronización; agrega reputación y mejora la alerta de pedidos.

## Alertas de pedidos
- Seller Center web: cartel rojo en primer plano durante 10 segundos.
- 10 ciclos de parpadeo (20 alternancias de 500 ms).
- 10 sonidos separados por 1 segundo.
- Se eliminó el beep duplicado que podía producir 11 sonidos.
- Windows: mismo comportamiento aproximado durante 10 segundos mediante Timer de interfaz.

## Reseñas
- Endpoint GET `/api/reviews?storeId=...`.
- Endpoint POST `/api/reviews/save`.
- Requiere sesión de comprador.
- Una reseña por comprador y tienda; una nueva publicación actualiza la existente.
- Puntuación entera de 1 a 5.
- Promedio mostrado con un decimal.
- Comentario máximo 600 caracteres.
- Emoji opcional.
- Se muestran las últimas 20 reseñas.
- El contenido mostrado en el navegador se escapa para evitar HTML inyectado.

## Catálogo móvil
La tienda pública usa 3 columnas en pantallas pequeñas, con imágenes más grandes para mejorar la lectura.

## Persistencia
Se agregó `nexomarket_reviews.xml`. Se restaura desde R2 si está configurado y se guarda junto con el resto de datos operativos.

## Compatibilidad
No se cambiaron las APIs de sincronización existentes ni se agregaron dependencias NuGet nuevas.
