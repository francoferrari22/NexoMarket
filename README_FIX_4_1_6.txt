NexoMarket 4.1.6 - FIX LICENCIA + ACCESO WEB + SINCRONIZACION RENDER

CAMBIOS PRINCIPALES

1) LICENCIA DEL VENDEDOR
- El programa Windows sigue siendo para vendedores.
- La licencia de vendedor es de 30 dias por defecto en License Manager.
- La pantalla de licencia ahora permite PEGAR directamente el codigo/token.
- Ya no pide seleccionar un archivo .nexolicense para activar.
- Si no hay clave publica local, Windows la obtiene de NexoMarket Central en Render.
- Al validar correctamente el token, la ventana se cierra y el programa continua.
- Se mantiene la vinculacion de licencia con Store ID + Machine ID.
- Los compradores web NO necesitan licencia.

2) BOTON INGRESAR EN LA WEB LOCAL
- /login ahora tiene pagina GET real.
- /register ahora tiene pagina GET real.
- El boton Ingresar ya no apunta a #login.
- El menu muestra Ingresar y Crear cuenta.

3) RENDER: CUENTAS
- El servidor central ahora tiene cuentas centralizadas.
- GET/POST /login
- GET/POST /register
- /seller
- /buyer
- /logout
- Las cuentas se guardan en Data/nexomarket_accounts.xml y se respaldan en R2 cuando R2 esta configurado.

4) SINCRONIZACION WINDOWS -> RENDER
- El sincronizador ya no se detiene si web_api_url esta vacio.
- Usa automaticamente https://nexomarket-central.onrender.com.
- Sincroniza las cuentas WebUsers hacia Render.
- Sincroniza tienda, productos y promociones.
- El vendedor que crea su cuenta en Windows puede usar el mismo correo y contraseña en Render.
- El Seller Center de Render muestra los productos que ya llegaron al catalogo central para ese Store ID.

5) LICENCIA EN RENDER
- GET /api/licenses/public-key devuelve la clave publica configurada en Render.
- Windows usa ese endpoint cuando activa un token pegado manualmente.

DEPLOY
1. Reemplazar el contenido del repositorio GitHub por este proyecto.
2. Commit + push.
3. En Render: Manual Deploy -> Deploy latest commit.
4. Verificar que el servicio quede Live.
5. Abrir https://nexomarket-central.onrender.com/ y hacer Ctrl+F5.

VARIABLES DE RENDER
Mantener configuradas:
PUBLIC_BASE_URL=https://nexomarket-central.onrender.com
LICENSE_ADMIN_KEY=...
LICENSE_PUBLIC_KEY_XML=...
R2_ACCOUNT_ID=...
R2_ACCESS_KEY_ID=...
R2_SECRET_ACCESS_KEY=...
R2_BUCKET=nexomarket
R2_PUBLIC_BASE_URL=...

PRUEBA RECOMENDADA
A) Windows: abrir NexoMarket Admin.
B) Si pide licencia: copiar el token de 30 dias y pegarlo en la nueva pantalla.
C) Confirmar que muestre Estado: Activa y Días restantes: 30 (o los dias correspondientes).
D) Abrir el panel y comprobar que el vendedor tenga la cuenta vinculada.
E) Esperar la sincronizacion o reiniciar la app.
F) Render: abrir /login, ingresar con el mismo correo/clave y comprobar /seller.
G) Comprobar que aparezcan los productos de la tienda sincronizada.
H) Abrir /register en una ventana privada y comprobar que se pueda crear una cuenta de comprador sin licencia.

NOTA
No se puede compilar el proyecto Windows en este entorno porque no hay SDK .NET Framework/MSBuild instalado. El proyecto queda preparado para compilar en Windows con Visual Studio/Build Tools. El Dockerfile de Render sigue usando .NET 8 y realiza el restore/publish durante el deploy.

=== 4.1.7 - LICENCIA POR CUENTA (60 DIAS) ===

- La licencia del vendedor ya no depende del Machine ID.
- La primera creación/entrada de una cuenta de vendedor activa 90 días en NexoMarket Central.
- La fecha de inicio y vencimiento se guardan en la cuenta central y no se reinician al cambiar de PC.
- La misma cuenta de vendedor puede iniciar sesión en otra computadora y conserva la misma fecha de vencimiento.
- Compradores no requieren licencia.
- Windows publica la cuenta local a Central antes de consultar/activar el período, sin esperar al sincronizador periódico.
- El botón de licencia ya no solicita token para el período inicial: muestra la licencia de cuenta y permite continuar.
- El License Manager antiguo queda como herramienta de compatibilidad para tokens históricos; el período inicial de 90 días se gestiona automáticamente por cuenta.
