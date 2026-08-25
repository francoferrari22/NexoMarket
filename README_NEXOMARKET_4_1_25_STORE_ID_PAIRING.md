# NexoMarket 4.1.25 — Store ID Pairing

## Nuevo modelo de vendedor

La identidad común de una tienda es su **Store ID**. Windows ya no depende de una conexión a Render para poder iniciar el programa.

### Windows-first
1. Windows crea la cuenta local del vendedor.
2. La instalación genera y muestra su Store ID.
3. El vendedor puede continuar trabajando aunque Central/Render esté temporalmente fuera de línea.
4. Ese mismo Store ID se copia en el registro de vendedor de NexoMarket Web.
5. Central crea o recupera la misma tienda; no se crea una segunda tienda.
6. Cuando Windows vuelva a sincronizar, adopta la configuración y la clave canónica de Central.

### Web-first
1. El vendedor crea la cuenta en NexoMarket Web.
2. Puede crear una tienda nueva o pegar el Store ID generado por Windows.
3. Si el Store ID ya existe, la cuenta se vincula a esa tienda.
4. Si todavía no existe en Central, Web puede crear la tienda con ese mismo Store ID.
5. Windows la descubre en el siguiente ciclo de sincronización.

## Una sola identidad vendedora por tienda

Central considera `StoreId` la identidad canónica del vendedor. Si Web y Windows usaron correos distintos para la misma tienda, el registro central no crea dos vendedores paralelos: mantiene una sola cuenta vendedora canónica por Store ID y Windows la adopta en la siguiente sincronización.

## Sincronización

Windows sincroniza en segundo plano. Primero descarga la cuenta central vinculada al Store ID y después publica la copia local, evitando que una cuenta local antigua sobrescriba una cuenta web recién vinculada.

Productos, promociones, pedidos y configuración siguen asociados al mismo Store ID.

## Licencias

El sistema de licencias continúa eliminado de esta versión.

## Compatibilidad

- NexoMarket Admin: .NET Framework 4.8, AnyCPU.
- NexoMarket Central: .NET 8 para Render.
- La arquitectura de Windows no introduce dependencias de Windows 10/11 exclusivas.
