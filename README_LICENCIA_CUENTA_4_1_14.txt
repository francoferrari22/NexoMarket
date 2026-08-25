NexoMarket 4.1.14 - Licencia por cuenta

CORRECCION PRINCIPAL
- La licencia se identifica por CUENTA (correo + ID de cuenta derivado del correo).
- Store ID ya no es obligatorio para generar ni activar una licencia.
- El Store ID queda como dato informativo de la tienda.
- Si una instalación existente no tenía seller_account_id, el programa lo genera y guarda automáticamente a partir del correo del vendedor.
- El License Manager puede buscar por correo o ID de cuenta.
- Al generar una licencia, solo son obligatorios Cliente/comercio, correo e ID de cuenta.
- Los tokens nuevos se generan sin Store ID para evitar que una migración de tienda bloquee la activación.
- El servidor acepta tokens de cuenta sin exigir coincidencia de Store ID.
- Los tokens anteriores siguen siendo válidos si coinciden con la misma cuenta (correo + ID) y la firma es válida.
- Machine ID no participa en la licencia.

FLUJO
1. Vendedor crea/inicia sesión.
2. Primera entrada: Render fija 90 días para esa cuenta.
3. Windows muestra el ID de cuenta y permite copiarlo.
4. Para licencia paga, el vendedor envía el ID de cuenta (Store ID no necesario).
5. License Manager busca la cuenta, genera el token, permite COPIAR TOKEN y GUARDAR TOKEN.
6. Vendedor pega el token y activa.
