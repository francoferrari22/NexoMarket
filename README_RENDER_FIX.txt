NexoMarket 4.1.4 - FIX RENDER

Corrección principal:
- El servidor ya no depende de Console.ReadLine().
- En Render, stdin puede estar cerrado y eso hacía que la aplicación terminara inmediatamente.
- Ahora el proceso queda ejecutándose permanentemente mientras el servidor esté activo.
- Se mantiene PORT de Render y escucha mediante TcpListener en todas las interfaces.
- /health sigue disponible para el health check.
- El log de API muestra la URL pública cuando PUBLIC_BASE_URL está configurada.

Para Render:
1. Subir/reemplazar el proyecto con este ZIP.
2. Hacer Manual Deploy / Deploy latest commit.
3. Esperar a que indique Live.
4. Abrir la URL pública del servicio.
