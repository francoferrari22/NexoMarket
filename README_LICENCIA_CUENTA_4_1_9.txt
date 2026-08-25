NexoMarket 4.1.9 - Licencia por cuenta

CAMBIO FUNDAMENTAL
- Se elimina Machine ID del flujo nuevo de licencias.
- Vendedor: 60 dias de prueba automaticos, iniciados una sola vez al crearse/entrar por primera vez la cuenta.
- Comprador: no requiere licencia.
- El ID usado para licencias es deterministico y depende de la cuenta/correo, no de la PC.
- La misma cuenta conserva la misma licencia en otra computadora.

FLUJO DE PRUEBA
1. Crear cuenta como vendedor en Windows o en Render.
2. Al primer ingreso del vendedor, Central fija TrialStartedUtc y TrialExpiresUtc = +60 dias.
3. Windows consulta /api/accounts/ensure-trial y entra directamente si esta Activa.
4. Si el vendedor cambia de PC, la misma cuenta conserva el vencimiento.

FLUJO DE LICENCIA COMPRADA
1. El vendedor abre Licencia en Windows o el panel web /seller.
2. Copia su ID de cuenta.
3. El administrador abre NexoMarket License Manager.
4. Ingresa ID de cuenta, correo y Store ID, elige duracion y crea el codigo.
5. Puede COPIAR TOKEN y GUARDAR TOKEN.
6. El vendedor pega el token en Windows o en el panel web.
7. Central verifica la firma y activa PaidLicenseExpiresUtc para esa cuenta.

IMPORTANTE PARA RENDER
- LICENSE_ADMIN_KEY debe estar configurada para que License Manager registre licencias.
- LICENSE_PUBLIC_KEY_XML debe contener la clave publica correspondiente al License Manager.
- R2 debe estar configurado para persistir cuentas, tiendas y catalogo.
