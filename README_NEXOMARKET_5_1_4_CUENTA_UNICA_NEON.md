# NexoMarket 5.1.4 — Cuenta única Web ↔ Windows + Neon Dark

## Flujo definitivo de vendedor
- Windows solicita únicamente correo y contraseña.
- El Store ID nunca se pide para iniciar sesión; se obtiene de la cuenta central.
- Si la cuenta no existe, Windows abre `https://nexomarket-0k22.onrender.com/seller-register` para crearla en la Web.
- La cuenta creada en Web puede utilizarse inmediatamente en Windows con el mismo correo y contraseña.
- El código de vinculación queda como método legado/alternativo, no como requisito de acceso.
- El servidor valida las credenciales contra PostgreSQL cuando está disponible.

## Seller Web
- Nuevo flujo `/seller-register` exclusivamente para vendedores.
- Crea Store ID automáticamente.
- No solicita Store ID ni código al vendedor.
- Después del alta redirige al Seller Center.

## UI
- Windows: negro profundo, tarjetas oscuras y acentos blanco/neón verde.
- Seller Center: fondo negro absoluto, bordes sutiles, botones blancos con glow y verde neón.
- Se conserva la funcionalidad existente.

## Deploy
1. Publicar el repositorio en Render.
2. Ejecutar Clear build cache & deploy.
3. Comprobar `/health` y `/api/central/status`.
4. Probar `/seller-register`.
5. Probar el mismo correo/contraseña desde Windows.
