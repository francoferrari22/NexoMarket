NexoMarket 5.13.1 - corrección de compilación

Se corrigió CentralServerService.cs en SellerOrderDetail.
La expresión regular de ItemsJson estaba escrita como cadena verbatim con secuencias \\" inválidas (CS1009 Unrecognized escape sequence).
Se reemplazó por una cadena verbatim C# correctamente escapada con comillas dobles.

No se modificó la lógica de sincronización de catálogo, stock, pedidos, sesiones ni syncKey.
