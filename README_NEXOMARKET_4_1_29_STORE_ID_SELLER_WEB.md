# NexoMarket 4.1.29 — Seller Web Store ID Login

## Cambio principal
El Seller Center web incorpora **Ingresar como vendedor con Store ID**.

El vendedor puede abrir `/seller-login`, ingresar únicamente el Store ID de su tienda y entrar al Seller Center centralizado.

### Flujo
1. NexoMarket Windows genera/identifica el Store ID.
2. El Store ID se sincroniza con NexoMarket Central.
3. En la web se selecciona **Ingresar como vendedor**.
4. Se introduce solamente el Store ID.
5. Central verifica que la tienda exista y esté activa.
6. Se crea una sesión de vendedor asociada a ese Store ID.
7. El Seller Center trabaja sobre el mismo catálogo, inventario, pedidos y datos centrales de la tienda.

No se crea una segunda tienda ni se exige correo/contraseña para el acceso operativo por Store ID.

## URL
`/seller-login`

## Seguridad
El Store ID funciona como código operativo de emparejamiento, tal como se solicitó. La sesión web se mantiene mediante cookie HttpOnly y el Store ID no se expone como credencial de sesión después del acceso.
