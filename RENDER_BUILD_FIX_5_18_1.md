# NexoMarket 5.18.1 — Render build fix

Se corrigieron errores CS1003/CS1012 producidos por comillas dobles no escapadas dentro de las cadenas C# que generan HTML/JavaScript en `CentralServerService.cs`, principalmente en el detalle de pedidos del vendedor y el seguimiento del comprador.

También se verificó léxicamente el archivo: strings, caracteres, comentarios y balance de `{}`, `[]`, `()` sin inconsistencias.

No se modificaron deliberadamente syncKey, emparejamiento Windows/Web, sincronización de catálogo, stock ni la ruta de recepción de pedidos.

La compilación final contra el SDK de .NET 8 debe ejecutarse en Render/Docker.
