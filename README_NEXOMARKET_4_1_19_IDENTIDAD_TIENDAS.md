# NexoMarket 4.1.19 — Identidad única de cuentas + tiendas activas

## Cambios principales

- La tienda Windows queda publicada/activa en Central desde la inicialización y al primer ciclo de sincronización.
- Las instalaciones existentes reciben una migración única que activa `store_web_active`.
- Windows publica primero la tienda y después la cuenta asociada al mismo `StoreId`.
- La identidad de la cuenta es el correo normalizado en NexoMarket Central.
- Windows puede autenticar una cuenta creada en la web mediante `/api/accounts/auth` y la importa localmente.
- Una cuenta vendedor conserva un único `StoreId`; no se crean cuentas paralelas por correo.
- El registro central de vendedor puede crear automáticamente una tienda nueva si el usuario todavía no tiene tienda.
- La página principal y Buyer Center muestran todas las tiendas activas; la ubicación sólo ordena por distancia.
- Se mantiene el sistema sin licencias.

## Flujo esperado

Windows -> publica tienda activa -> publica cuenta -> Central
Web -> crea/usa la misma cuenta Central -> Windows la importa al iniciar sesión

## Nota de persistencia

Render debe tener configurado el almacenamiento R2 para conservar `nexomarket_stores.xml` y `nexomarket_accounts.xml` entre reinicios/despliegues. Si R2 no está configurado, los datos sólo viven en el filesystem efímero del servicio.
