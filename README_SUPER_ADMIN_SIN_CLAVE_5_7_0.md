# NexoMarket Super Admin 5.7.0

Esta edición elimina la solicitud manual de `NEXOMARKET_ADMIN_KEY`. El Super Administrador se conecta directamente a los endpoints administrativos existentes.

## Importante
Los endpoints `/api/admin/*` quedan sin autenticación por clave. Esto simplifica el uso, pero reduce la seguridad si el servidor queda expuesto públicamente. Usar únicamente porque el propietario solicitó expresamente eliminar la clave y mantiene copias de respaldo.

No se requiere MSBuild para la herramienta HTA.
