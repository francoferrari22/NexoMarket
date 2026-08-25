# NexoMarket 5.0.5 — Build Fix

## Corrección puntual

Se corrigió `NexoMarket.CentralServer/CentralServerService.cs` en la línea que contiene el script de actualización en vivo del Seller Center.

El selector JavaScript contenía `value=""` dentro de una cadena C# sin escapar, provocando `CS1003` (`, expected`) y errores de sintaxis en cascada como `CS1513` y `CS1514`.

La cadena ahora usa las comillas escapadas correctamente.

## Despliegue

1. Reemplazar el contenido del repositorio por este proyecto.
2. Hacer commit/push.
3. En Render usar Manual Deploy → Deploy latest commit.
4. Mantener el PostgreSQL, Store ID, R2 y variables existentes.

Esta versión es un build-fix sobre 5.0.4; no requiere crear una cuenta nueva ni una base de datos nueva.
