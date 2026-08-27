# Render deploy fix – NexoMarket 5.16

Se corrigió un error de compilación introducido en `CentralServerService.cs`.

## Error observado
`CS1003 Syntax error` y `CS1012 Too many characters in character literal`
en la línea del HTML/JavaScript del carrito, al generar el campo de nota por producto.

## Corrección
Se escaparon correctamente las comillas dobles HTML/JavaScript dentro del literal C# que genera el carrito.

No se modificaron deliberadamente:
- syncKey
- emparejamiento Windows/Web
- endpoints de sincronización
- catálogo/inventario
- recepción de pedidos
- autenticación

## Importante
El ZIP conserva el Dockerfile existente y el comando `dotnet build -c Release --no-restore`.
