# NexoMarket 4.1.32 — SINCRONIZACIÓN CENTRAL INTELIGENTE 20s

## Objetivo
Windows y Seller Center Web trabajan sobre el mismo Store ID y la misma fuente central. La sincronización automática ahora funciona en ciclos de 20 segundos, sin mantener puertos abiertos en la PC.

## Cambios
- Intervalo de sincronización Windows: 20 segundos.
- Central sigue siendo la fuente de verdad.
- Nuevo endpoint `GET /api/sync/delta`.
- Windows guarda un cursor `central_sync_cursor` y descarga solamente productos modificados desde ese cursor.
- En la primera sincronización se descarga el catálogo completo de la tienda; las siguientes son incrementales.
- Las publicaciones de productos desde Windows se limitan a productos modificados desde el último cursor.
- La configuración de la tienda ya no se publica ciegamente en cada ciclo.
- Windows primero adopta cambios de tienda realizados desde la Web y luego publica solo cambios locales reales.
- Se mantiene el endpoint central `https://nexomarket-0k22.onrender.com`.
- Se conserva compatibilidad con el proyecto WinForms/.NET Framework existente y AnyCPU.
- Se conserva R2 como almacenamiento persistente central del proyecto actual.

## Flujo
1. Web modifica producto/tienda.
2. Central guarda el cambio.
3. Windows consulta cada 20 segundos.
4. Windows recibe solo cambios posteriores al cursor.
5. Si Windows modifica un producto, lo publica en Central.
6. La Web lee la misma información central.

## Importante
Este mecanismo es sincronización periódica, no WebSocket. El tiempo máximo normal de propagación desde un extremo al otro es de aproximadamente 20 segundos, además del tiempo de red/procesamiento.
