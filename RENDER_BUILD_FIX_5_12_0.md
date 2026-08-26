# NexoMarket 5.12.0 — Render build fix

## Corrección crítica

El deploy anterior fallaba en `NexoMarket.CentralServer/CentralServerService.cs` por un literal C# mal escapado dentro de la vista de configuración de la tienda online.

El código problemático generaba una etiqueta HTML con comillas sin escapar dentro de una cadena C# y provocaba la cascada de errores:

- CS1002 `; expected`
- CS1012 `Too many characters in character literal`
- CS1003 `Syntax error`

La vista quedó corregida usando comillas escapadas dentro del literal C#.

## Docker / Render

El Dockerfile ahora hace un único `dotnet publish` en Release después de `dotnet restore`. Esto elimina el build redundante y deja que el publish sea la única etapa de compilación del servidor.

Render sigue usando Docker y no requiere MSBuild en la computadora local.

## Sincronización

Se reforzó la distribución de pedidos:

- Los pedidos activos/recentes ya no dependen exclusivamente del ACK global de una instalación Windows.
- Windows puede reconciliar nuevamente el pedido sin duplicarlo gracias a `CentralOrderId`.
- El cursor de pedidos conserva una pequeña ventana de solapamiento para no perder pedidos con marcas de tiempo iguales o muy cercanas.
- StoreId + SyncKey siguen siendo la identidad común Web ↔ Windows.
- Un token de dispositivo inválido se limpia y se repara sin romper la sincronización.

## Validación local realizada

- El JavaScript de configuración de tienda afectado pasó `node --check` después de la corrección.
- Se revisó la ruta de Render y el Dockerfile.
- No se pudo ejecutar `dotnet publish` en este entorno porque no hay SDK .NET instalado fuera del contenedor de Render.
