# NexoMarket 5.0.1 — Identidad Central Unificada

Esta versión completa la capa de identidad sobre PostgreSQL:

- una cuenta vendedora canónica por correo;
- Store ID asociado a la tienda;
- Device ID estable por instalación Windows;
- pairing temporal de 10 minutos;
- QR/código para vincular Windows desde Seller Center;
- token de dispositivo almacenado localmente y validado contra Central;
- Windows sigue usando caché local, pero la identidad y autorización pertenecen a Central.

## Flujo recomendado

1. Crear la tienda y la cuenta vendedora en NexoMarket Web o registrar la cuenta desde Windows con el mismo Store ID.
2. Entrar al Seller Center con correo + contraseña + Store ID.
3. Seller Center → Dispositivos / QR → generar vínculo.
4. En Windows, ingresar el código temporal.
5. Central registra Store ID + Device ID + cuenta y entrega un token de dispositivo.
6. En cada ciclo de sincronización Windows valida el Device ID/token antes de operar.

El Store ID ya no es una contraseña: identifica la tienda. La contraseña autentica la cuenta y el Device ID/token autoriza una instalación concreta.
