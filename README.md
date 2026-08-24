# NexoMarket 4.2 — Cloud First 4.1.4

NexoMarket queda separado de FerrariPOS.

## Licencias: código de activación

El License Manager ya no necesita entregar un archivo al vendedor.

1. Cargar Cliente, Store ID y Machine ID.
2. Elegir 30 / 90 / 365 días / Permanente.
3. Pulsar `CREAR / RENOVAR`.
4. El campo `CÓDIGO DE ACTIVACIÓN` genera un código `NLM1-...`.
5. `COPIAR CÓDIGO` copia el código al portapapeles.
6. El vendedor puede pegarlo en Windows en la ventana de licencia o en `Seller Center → Licencia`.

El código está firmado digitalmente. Windows comprueba Store ID + Machine ID + firma.

## Prueba inicial

La primera ejecución de NexoMarket habilita una prueba automática de 30 días. La fecha se inicia una sola vez en el primer arranque.

## Cuentas

El correo es único en NexoMarket. El registro consulta el servidor central antes de crear la cuenta local. Un correo existente no se convierte en un segundo registro: debe iniciar sesión o recuperar contraseña.

## Recuperación

La web genera un código de recuperación de 6 dígitos con vencimiento de 10 minutos. Puede usar el SMTP configurado en Windows o el relay de correo central.

## Web

- `/login` abre directamente el login.
- El botón `Ingresar` de la navegación apunta directamente a `/login`.
- El Seller Center incluye activación por código.
- Nuevo producto permite `SACAR FOTO` mediante la cámara nativa del dispositivo (`capture=environment`) y también cámara directa cuando el navegador/HTTPS lo permite.
- Checkout incluye campo de cupón y valida fecha, estado y límite de usos.

## Persistencia y archivos

Render ejecuta el servidor. Para mantener el costo en cero se usa la instancia Free; esta instancia puede entrar en suspensión después de 15 minutos sin tráfico y puede tardar alrededor de un minuto en despertar. Los datos definitivos NO dependen del disco local: R2 conserva los objetos y las copias de los XML centrales.

- R2: imágenes, logos, fotos de productos, promociones y comprobantes.
- R2 también conserva copias de los XML centrales de cuentas, catálogo, pedidos, licencias y tiendas.
- La API central restaura los XML desde R2 si el contenedor vuelve a arrancar sin datos locales.

## Render

Variables necesarias:

- `PUBLIC_BASE_URL`
- `LICENSE_ADMIN_KEY`
- `LICENSE_PUBLIC_KEY_XML`
- `R2_ACCOUNT_ID`
- `R2_ACCESS_KEY_ID`
- `R2_SECRET_ACCESS_KEY`
- `R2_BUCKET`
- `R2_PUBLIC_BASE_URL`
- `MEDIA_UPLOAD_KEY`
- `EMAIL_RELAY_KEY`
- `SMTP_USER`
- `SMTP_APP_PASSWORD`
- `SMTP_HOST`
- `SMTP_PORT`
- `SMTP_SSL`

Para Internet público se debe usar HTTPS y un dominio fijo, por ejemplo `api.nexomarket.app`, en lugar de depender de la IP de una PC. La PC del vendedor puede permanecer apagada.

## Herramientas

### Admin

- `Herramientas/Admin/COMPILAR_FACIL_ADMIN.bat`
- `Herramientas/Admin/CREAR_SETUP_ADMIN.bat`
- `Herramientas/Admin/COMPILAR_Y_CREAR_SETUP_ADMIN.bat`

### License Manager

- `Herramientas/LicenseManager/COMPILAR_FACIL_LICENSE_MANAGER.bat`
- `Herramientas/LicenseManager/CREAR_SETUP_LICENSE_MANAGER.bat`
- `Herramientas/LicenseManager/COMPILAR_Y_CREAR_SETUP_LICENSE_MANAGER.bat`

No se necesitan los TXT históricos de correcciones para compilar.


## Arquitectura cloud 4.2

El servidor central puede ejecutarse en Render sin depender de la PC del vendedor. En Free queda disponible públicamente pero puede dormir por inactividad; para disponibilidad 24/7 garantizada se necesita una instancia que no se suspenda. Los datos centrales se replican en Cloudflare R2 cuando R2 está configurado. El Seller Center está disponible en `/seller` y el login en `/seller/login`.

La PC Windows queda como cliente/sincronizador y no como servidor público obligatorio.
