NexoMarket 4.1.16 - LICENCIA POR CUENTA / PRUEBA 90 DIAS

CAMBIOS PRINCIPALES
- La prueba del vendedor es de 90 días.
- Los 90 días se asignan automáticamente al crear la cuenta de vendedor.
- El inicio y vencimiento de la prueba quedan guardados en NexoMarket Central.
- El contador pertenece a la cuenta, no a la computadora.
- Machine ID no participa en la licencia.
- Store ID no participa en la identidad de la licencia; queda solo como dato de tienda.
- Si una cuenta de una versión anterior tenía una prueba de exactamente 60 días y seguía en prueba, se migra una sola vez a 90 días sin reiniciar la fecha de inicio.
- Los compradores no necesitan licencia.
- El vendedor puede copiar únicamente su ID de cuenta para solicitar una licencia.
- El token de licencia comprada se valida por ID de cuenta + correo y se guarda en el servidor.
- Windows muestra botones para copiar ID, pegar/activar token, copiar token y guardar token.
- El panel web de vendedor también permite pegar el token.
- La información de cuentas/licencias se persiste en R2 cuando R2 está configurado.

FLUJO DEFINITIVO
1. Vendedor crea cuenta.
2. Central crea TrialStartedUtc y TrialExpiresUtc = +90 días automáticamente.
3. Windows entra con esa misma cuenta y consulta el estado.
4. El vendedor puede copiar su ID de cuenta.
5. Para comprar, envía ese ID.
6. El administrador genera un token firmado para esa cuenta.
7. El vendedor pega el token en Windows o en el panel web.
8. La licencia comprada queda asociada a la cuenta y funciona al iniciar sesión desde otra PC.

IMPORTANTE
- No se debe volver a pedir Machine ID.
- No se debe pedir Store ID para activar una licencia.
- No se deben activar manualmente los 90 días: ya vienen incluidos en la cuenta.
