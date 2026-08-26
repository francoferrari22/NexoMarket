NEXOMARKET 5.3.0 · SUPER ADMINISTRADOR
======================================

Se agregó una herramienta independiente para que el propietario de NexoMarket pueda administrar la plataforma completa sin entrar a una tienda.

1) BACKEND / RENDER
-------------------
Agregar en Render la variable:
NEXOMARKET_ADMIN_KEY = una clave maestra larga y privada.

No compartir esta clave con vendedores ni compradores.

El backend expone endpoints protegidos mediante el header X-Nexo-Admin-Key.

2) SUPER ADMIN WINDOWS
----------------------
Carpeta: NexoMarket.SuperAdmin
Proyecto: NexoMarket.SuperAdmin.sln
Compilación: COMPILAR_FACIL_SUPERADMIN.bat
Requisito: Visual Studio/Build Tools + .NET Framework 4.8.

Funciones:
- Ver todas las tiendas.
- Ver todas las cuentas.
- Crear tiendas.
- Crear cuenta vendedora al crear tienda.
- Asignar días de prueba por correo.
- Activar/bloquear cuentas.
- Activar/bloquear tiendas.
- Eliminar tienda de raíz.
- Eliminar cuentas.
- Vaciar toda la plataforma con confirmación.

3) ELIMINACIÓN DE RAÍZ
----------------------
Al eliminar una tienda se quitan:
- registro de la tienda;
- cuenta vendedora asociada;
- dispositivos;
- emparejamientos;
- productos/promociones/cupones asociados;
- pedidos asociados;
- medios R2 bajo stores/<StoreId>/.

4) PRUEBAS
----------
Las cuentas tienen active y trial_expires_at. Si la cuenta está bloqueada o su prueba venció, el login no permite acceso.

5) PROYECTO SIN DATOS
---------------------
El código fuente entregado NO contiene tiendas ni cuentas sembradas.
Si el PostgreSQL de Render ya tiene datos de una instalación anterior, usar el Super Admin -> VACIAR TODO, o ejecutar RESET_NEXOMARKET_DATOS.sql.

NO ejecutar el reset sobre una instalación que deba conservar datos.
