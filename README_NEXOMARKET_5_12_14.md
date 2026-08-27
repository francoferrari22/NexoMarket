# NexoMarket 5.12.14 — corrección raíz de pedidos Buyer Web → Seller Center Web

## Objetivo
Corregir la desconexión por la que un comprador recibía "pedido enviado" pero el Seller Center quedaba vacío y no activaba la alerta.

## Correcciones aplicadas
- El Seller Center resuelve y refresca el `StoreId` canónico de la cuenta vendedora en cada sesión.
- Se reconcilian cuentas seller existentes en PostgreSQL con la identidad de tienda persistida en XML legado cuando corresponde.
- La búsqueda del vendedor por tienda da prioridad a la identidad persistida de la cuenta seller y sincroniza la cuenta central.
- `LoadOrdersForSeller` combina PostgreSQL y XML, deduplica por `CentralOrderId` y no utiliza un fallback "todo o nada" que pudiera ocultar pedidos.
- `/api/seller/live` devuelve el conjunto de pedidos que la sesión seller realmente puede ver, junto con KPIs y el estado de la tienda/cuenta.
- El dashboard Seller Center deja de hacer `location.reload()` para detectar pedidos nuevos.
- El dashboard consulta `/api/seller/live` silenciosamente cada 1,8 s con cache-buster y sin solicitudes simultáneas.
- Los KPIs "Pedidos pendientes" y "Delivery" se actualizan en el DOM sin recargar ni parpadear la página.
- La vista de pedidos incorpora nuevos pedidos al DOM en forma silenciosa y sincroniza estado/pago de los existentes.
- La detección de pedidos nuevos se hace por `CentralOrderId`, evitando falsos positivos al cambiar de estado un pedido antiguo.
- Se conserva la alerta visual, sonido, notificación del navegador y 10 ciclos de parpadeo.
- `OrderJson` incluye `ack` para mantener la identidad de recepción.
- Se conserva SuperAdmin y el resto del proyecto; el antiguo panel Windows no forma parte del paquete.

## Endpoint de verificación
`GET /api/version`

Debe responder versión `5.12.14`.

## Comprobaciones estáticas realizadas
- Sintaxis JavaScript del live dashboard: `node --check` OK.
- Balance básico de llaves/cadenas/paréntesis del C# modificado: OK.
- No existe `location.reload` en el script de recepción de pedidos.
- No existe `ERROR|seller_account_not_found` en el flujo de checkout.
- No se eliminaron módulos web ni SuperAdmin.

## Limitación
Este entorno no tiene instalado el SDK de .NET, por lo que no se puede ejecutar aquí un `dotnet build` ni una prueba real contra la instancia de Render/PostgreSQL desplegada.
