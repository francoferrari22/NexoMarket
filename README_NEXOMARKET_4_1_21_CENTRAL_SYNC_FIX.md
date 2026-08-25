# NexoMarket 4.1.21 - Central Sync Fix

Corrección de la versión 4.1.20.

## Correcciones

- Se agregó la función `IsLegacyLocalUrl` que faltaba en `CentralSyncService.cs`.
- Se mantiene el endpoint central de Render como:
  `https://nexomarket-central.onrender.com`
- Las instalaciones antiguas que apuntaban a localhost o IP LAN se migran al endpoint central.
- La publicación de tiendas registra el resultado real de la operación.
- Los errores de sincronización ya no quedan completamente silenciosos: se guardan en `central_sync_last_error`.
- Se aumentó el timeout HTTP para tolerar el arranque en frío de Render.
- La identidad sigue siendo `Email -> Account -> StoreId -> Store`.
- Se mantienen deshabilitadas las licencias.
- Se mantiene la compatibilidad del cliente Windows con .NET Framework/Windows 8+.

## Render

El servicio central utiliza `PUBLIC_BASE_URL` y las variables R2 definidas en `render.yaml`.

Para persistencia entre reinicios de Render deben estar configuradas:
- R2_ACCOUNT_ID
- R2_ACCESS_KEY_ID
- R2_SECRET_ACCESS_KEY
- R2_BUCKET
- R2_PUBLIC_BASE_URL (si se usan URLs públicas para multimedia)

## Prueba

1. Ejecutar Windows.
2. Guardar configuración de tienda/web.
3. Crear o modificar la tienda.
4. Esperar el ciclo de sincronización o reiniciar el cliente.
5. Abrir `https://nexomarket-central.onrender.com`.
6. La tienda debe aparecer en el directorio.
7. Crear/modificar productos y verificar `/api/catalog?storeId=...`.
8. Crear una cuenta de vendedor y verificar el ingreso desde Render y Windows con el mismo correo/contraseña.

Nota: el SDK de .NET no está disponible en el entorno de generación de este ZIP, por lo que no se afirma una compilación local aquí.
