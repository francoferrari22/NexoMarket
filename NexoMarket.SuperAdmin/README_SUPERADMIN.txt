NEXOMARKET SUPER ADMINISTRADOR 5.3.0
===================================

Herramienta Windows independiente para administrar la plataforma NexoMarket.

FUNCIONES
- Ver todas las tiendas y su vendedor.
- Crear una tienda y opcionalmente una cuenta vendedora.
- Asignar dias de prueba a una cuenta por correo.
- Activar o bloquear cuentas.
- Activar o bloquear tiendas.
- Eliminar una tienda de raiz: tienda, cuenta, dispositivos, emparejamientos, catalogo/pedidos asociados y medios R2 bajo stores/<StoreId>/.
- Vaciar toda la plataforma con confirmacion NEXO-FACTORY-RESET.
- Ver resumen de tiendas, tiendas activas y cuentas.

SEGURIDAD
La herramienta NO guarda la clave maestra en el codigo. La clave se configura en Render como:
NEXOMARKET_ADMIN_KEY

La misma clave se escribe en la pantalla de conexion de esta herramienta.

COMPILACION
Ejecutar COMPILAR_FACIL_SUPERADMIN.bat en Windows con Visual Studio/Build Tools y .NET Framework 4.8.

IMPORTANTE
El boton VACIAR TODO es destructivo. Usarlo solamente cuando se quiera dejar la plataforma sin tiendas ni cuentas.
