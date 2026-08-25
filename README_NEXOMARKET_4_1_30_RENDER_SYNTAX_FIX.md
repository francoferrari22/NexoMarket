# NexoMarket 4.1.30 — Render Syntax Fix

Corrección del error de compilación mostrado por Render:

- CS1002 `; expected`
- CS1513 `} expected`

La causa estaba en la construcción de HTML/JavaScript inline dentro de `CentralServerService.cs`, donde las comillas escapadas de los atributos `onclick` podían romper el string C#.

Se reconstruyeron esas dos secciones usando atributos HTML con comillas simples y cadenas C# separadas, manteniendo exactamente el comportamiento del carrito y promociones.

La estructura de Store ID Seller Web se conserva.
