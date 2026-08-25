# NexoMarket 4.1.28 — Render Build Fix

- Se separa restore/build/publish para que Render muestre el error real si la compilación falla.
- El código central de 4.1.27 se conserva.
- No se cambia la compatibilidad del programa Windows.
- El servicio central continúa en .NET 8.
- El publish usa `--no-build` después de un build exitoso, evitando ocultar el diagnóstico.

Importante: la captura de Render mostraba solamente el wrapper `failed to solve`; no mostraba la línea de error de C# anterior. Esta versión fuerza a Render a mostrarla si todavía existe algún error.
