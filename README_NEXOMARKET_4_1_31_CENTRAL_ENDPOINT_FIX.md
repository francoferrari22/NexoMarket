# NexoMarket 4.1.31 — CENTRAL ENDPOINT FIX

## Problema corregido
La web utilizada estaba publicada en `https://nexomarket-0k22.onrender.com`, mientras que Windows seguía apuntando a `https://nexomarket-central.onrender.com`. Por eso Windows y la web hablaban con servidores/direcciones diferentes: el Store ID existía en un lado y no en el otro.

## Corrección
- Windows usa `https://nexomarket-0k22.onrender.com` como endpoint central por defecto.
- `NexoMarketCentral.url` fue actualizado.
- `PUBLIC_BASE_URL` de Render fue actualizado.
- Las instalaciones existentes migran automáticamente el endpoint antiguo `nexomarket-central.onrender.com` al nuevo.
- Se conserva el Store ID como identidad única.
- La tienda queda activa por defecto.
- Windows vuelve a intentar `connect` y, si la tienda no existe en Central, hace `claim` por Store ID.
- El Seller Center `/seller-login` consulta exactamente el mismo registro central.
- Si un Store ID válido todavía no fue publicado por Windows, `/seller-login` crea un registro central mínimo con ese mismo Store ID y Windows lo completa en su siguiente sincronización.

## Prueba
1. Deploy de este commit en el servicio Render que actualmente sirve `nexomarket-022.onrender.com`.
2. Abrir Windows con el Store ID existente.
3. Esperar la sincronización.
4. Comprobar que la tienda aparezca en `/api/stores` y `/seller-login`.
5. Ingresar en Web con el mismo Store ID.
6. Crear un producto en Windows y comprobar `/api/catalog?storeId=...`.
