# NexoMarket 4.2.2 — despliegue final

## Objetivo
La PC del vendedor NO es el servidor público. El marketplace, Seller Center, cuentas, licencias, pedidos y multimedia viven en el servicio central. Windows funciona como cliente/sincronizador.

## Persistencia local
NexoMarket Admin usa `%LOCALAPPDATA%\NexoMarket\Admin\Data`. License Manager usa `%LOCALAPPDATA%\NexoMarket\LicenseManager\Data`. Esto queda fuera de la carpeta de instalación, por lo que desinstalar/reinstalar no borra cuentas, configuración, Store ID, prueba/licencia ni multimedia local. Al actualizar, se migran datos de una instalación antigua si todavía existen.

## Persistencia cloud
- Datos centrales: archivos XML respaldados en Cloudflare R2 en cada escritura y restaurados al iniciar si el contenedor no los tiene.
- Imágenes/archivos: R2.
- R2_PUBLIC_BASE_URL debe apuntar al dominio público del bucket o a un dominio CDN de R2.

## Licencias
El código NLM1 nuevo es autocontenido: lleva la clave pública necesaria para verificar la firma. Windows puede validarlo incluso si todavía no tiene `license_public_key.xml` ni conexión al servidor. El servidor, cuando `LICENSE_PUBLIC_KEY_XML` está configurada, exige esa clave como raíz de confianza.

## Render
El servicio está configurado como `free`, no `free`, porque la versión Free se duerme después de 15 minutos sin tráfico y su filesystem es efímero. Para producción siempre activa hay que usar una instancia paga.

`PUBLIC_BASE_URL` debe quedar en producción como `https://nexomarket.app` cuando el dominio haya sido añadido a Render.

## Dominio
No hace falta una IP pública fija de la PC del vendedor. El dominio estable es la dirección permanente. Render asigna una URL `onrender.com` y permite dominio propio + TLS.

## Variables secretas
Configurar en Render, nunca dentro del repositorio:
- LICENSE_ADMIN_KEY
- LICENSE_PUBLIC_KEY_XML
- R2_ACCOUNT_ID
- R2_ACCESS_KEY_ID
- R2_SECRET_ACCESS_KEY
- R2_BUCKET
- R2_PUBLIC_BASE_URL
- EMAIL_RELAY_KEY
- SMTP_USER
- SMTP_APP_PASSWORD
- MEDIA_UPLOAD_KEY

## Dominio y DNS
1. Crear el Web Service desde el repositorio.
2. Añadir `nexomarket.app` en Custom Domains.
3. En Cloudflare DNS, apuntar el dominio al destino que Render indique.
4. Eliminar registros AAAA si existen durante la configuración.
5. Usar SSL/TLS `Full` en Cloudflare.
6. Verificar `https://nexomarket.app/health`.

## Prueba de aceptación
1. Crear una cuenta de vendedor.
2. Crear tienda y producto con foto.
3. Cerrar/desinstalar/reinstalar Windows.
4. Confirmar que la cuenta, Store ID, producto y multimedia local continúan.
5. Apagar completamente la PC del vendedor.
6. Desde un teléfono externo abrir `https://nexomarket.app`.
7. Confirmar que la tienda, imágenes, promociones, cupones y pedidos siguen funcionando.


## Importante sobre el plan gratuito
La instancia Free de Render es suficiente para pruebas y para mantener el servicio sin costo, pero puede suspenderse después de 15 minutos sin tráfico. El primer acceso posterior puede tardar aproximadamente un minuto. R2 mantiene los datos y archivos fuera del disco efímero.
