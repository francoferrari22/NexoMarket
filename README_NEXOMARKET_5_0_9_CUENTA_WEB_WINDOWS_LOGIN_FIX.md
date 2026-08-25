# NexoMarket 5.0.9 — Cuenta Web → Windows / Login independiente

## Cambios principales

- "YA TENGO CUENTA · INICIAR SESIÓN" abre una ventana independiente.
- Esa ventana solicita únicamente correo y contraseña.
- Windows autentica primero contra `/api/auth/login` (Seller Center) y mantiene `/api/accounts/auth` como compatibilidad.
- El Store ID se toma exclusivamente de la cuenta central autenticada; no se pide ni se usa el Store ID local para decidir la tienda.
- Al cambiar de cuenta se limpia el token de dispositivo anterior para evitar que una autorización vieja bloquee la sincronización.
- Windows intenta adoptar la `central_sync_key` de la tienda asociada a la cuenta sin hacer `claim` de una tienda local.
- Un fallo momentáneo de sincronización no impide iniciar sesión en el panel de Windows.
- El código de vinculación web → Windows ahora se genera como código corto de 6 dígitos, de un solo uso y con vencimiento de 10 minutos.
- Windows sigue aceptando códigos con guion, sin guion y el payload QR `NEXOMARKETPAIR:StoreId|codigo`.

## Flujo esperado

1. En Windows: Cuenta central → **YA TENGO CUENTA · INICIAR SESIÓN**.
2. Se abre una ventana independiente.
3. Se introduce el mismo correo y contraseña utilizados en Seller Center Web.
4. Central devuelve la cuenta de vendedor y su Store ID.
5. Windows adopta automáticamente ese Store ID.
6. El panel de Windows queda asociado a la misma cuenta/tienda.
7. La vinculación por código queda como alternativa para autorizar el dispositivo, no como requisito para entrar.
