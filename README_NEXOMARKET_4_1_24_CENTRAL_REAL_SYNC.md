# NexoMarket 4.1.24 — Central Real Sync

## Objetivo
Una misma tienda se identifica por `StoreId` y es compartida entre NexoMarket Windows y NexoMarket Web. Windows no solicita correo ni contraseña para la conexión de vendedor: solamente Store ID.

## Flujo
1. Web crea una tienda o Windows ya tiene una tienda local.
2. Windows ingresa únicamente el Store ID.
3. Windows consulta `GET /health` y `GET /api/stores/connect`.
4. Si la tienda existe, Central devuelve la identidad canónica y la clave interna de sincronización.
5. Si la tienda todavía no fue registrada, Windows usa `POST /api/stores/claim` para registrarla por Store ID y luego vuelve a conectarse.
6. Desde ese momento Windows publica catálogo/cuentas/promociones/pedidos y descarga cambios centrales periódicamente.

## Persistencia
Render debe tener R2 configurado para conservar tiendas, cuentas, catálogo y pedidos entre reinicios.

Variables de Render: `PUBLIC_BASE_URL`, `R2_ACCOUNT_ID`, `R2_ACCESS_KEY_ID`, `R2_SECRET_ACCESS_KEY`, `R2_BUCKET`, `R2_PUBLIC_BASE_URL` (esta última opcional para URLs públicas de archivos).

## Endpoint central
El cliente usa `NexoMarketCentral.url` junto al ejecutable si existe; si no, usa `https://nexomarket-central.onrender.com`. Esto evita tener que recompilar Windows si cambia la URL pública de Render.

## Seguridad
El Store ID es el identificador/capacidad de emparejamiento. La clave interna de sincronización nunca se pide al usuario y se usa para proteger catálogo, cuentas y promociones.

## Compatibilidad
La aplicación Windows mantiene `HttpWebRequest` + TLS 1.2 para conservar compatibilidad con el entorno Windows existente. El servidor central se publica como .NET 8 en Render.
