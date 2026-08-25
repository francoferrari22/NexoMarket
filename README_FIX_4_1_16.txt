NexoMarket 4.1.16 - CORRECCION DEFINITIVA LICENCIA POR CUENTA

1. Prueba automática de 90 días para vendedores.
2. Los 90 días nacen con la cuenta y se guardan en Central; no dependen de PC/Machine ID.
3. Migración de pruebas antiguas de exactamente 60 días a 90 días, sin reiniciar el inicio.
4. Store ID deja de ser requisito de licencia.
5. LicenseGate de Windows muestra ID de cuenta, días, vencimiento y botones:
   - Copiar ID de cuenta
   - Pegar / Activar código
   - Copiar token
   - Guardar token
   - Continuar
6. El token activado se guarda localmente y la licencia comprada se guarda en Central.
7. El comprador sigue sin necesitar licencia.
8. El panel web del vendedor muestra estado, días y vencimiento de la cuenta.
9. La web ya tiene /login y /register; el botón Ingresar apunta a /login.
10. La persistencia de cuentas/licencias usa R2 cuando está configurado.

COMPILACION
- Admin: Herramientas\Admin\COMPILAR_FACIL_ADMIN.bat
- License Manager: Herramientas\LicenseManager\COMPILAR_FACIL_LICENSE_MANAGER.bat
- Central/Render: Dockerfile incluido.
23. FIX RENDER CS0111:
   - CentralServerService.cs tenía dos métodos WriteRedirect(NetworkStream, string).
   - Se conserva la implementación segura que valida la URL y se elimina la copia duplicada.
   - Esto corrige el error CS0111 durante `dotnet publish`.
   - No se cambia el sistema de licencia por cuenta ni el período de prueba de 90 días.
