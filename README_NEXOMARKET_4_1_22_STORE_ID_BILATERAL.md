# NexoMarket 4.1.22 — STORE ID + SINCRONIZACIÓN BILATERAL

## Nuevo acceso de vendedor en Windows

Windows ya no solicita correo, contraseña ni otros datos para abrir el programa como vendedor.

El único dato solicitado es:

**STORE ID**

El Store ID es la identidad común entre la instalación Windows y la tienda de NexoMarket Central.

## Flujo definitivo

1. El vendedor crea su cuenta en NexoMarket Web.
2. Si no tiene tienda, Web crea la tienda y asigna un Store ID.
3. El Seller Center muestra el Store ID.
4. En Windows se ingresa solamente ese Store ID.
5. Windows consulta `/api/stores/connect` en Render.
6. Central valida que la tienda exista, esté activa y tenga una cuenta vendedor.
7. Windows recibe el Store ID, la identidad de la tienda y la clave de sincronización central.
8. Windows descarga las cuentas de esa tienda.
9. Windows sincroniza catálogo y pedidos con Central.
10. Los productos y promociones guardados en Windows se publican inmediatamente al central además del ciclo automático.

## Sincronización

- Ciclo automático: cada 30 segundos.
- Productos/promociones guardados en Windows: publicación inmediata.
- Cuentas: sincronización por Store ID.
- Pedidos web: descarga desde Central.
- Tienda: queda activa al conectarse por Store ID.
- La web sigue siendo el punto central para las tiendas activas.
- La ubicación solamente ordena las tiendas; no elimina las tiendas lejanas.

## Licencias

El sistema de licencias continúa fuera del proyecto, tal como se solicitó.

## Compatibilidad

Se mantiene el cliente Windows basado en Windows Forms/.NET Framework y `HttpWebRequest` con TLS 1.2 para conservar la compatibilidad del proyecto.
