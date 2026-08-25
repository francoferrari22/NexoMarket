# NexoMarket 5.1.1 — Saneamiento Central + Login Web/Windows

## Objetivo
Dejar el Central Server listo para una publicación limpia en Render y hacer que Windows use la cuenta Web como fuente de verdad.

## Cambios
- El login existente de Windows usa correo + contraseña y adopta automáticamente el Store ID de la cuenta Web.
- Un Store ID antiguo guardado localmente no puede bloquear una cuenta Web válida.
- Seller Center Web mantiene login por correo + contraseña y obtiene el Store ID desde la cuenta.
- Vinculación opcional mediante código de 6 dígitos, un solo uso y 10 minutos.
- El código de vinculación no es requisito para el login normal.
- Docker imprime SHA256 de los fuentes críticos antes de compilar, para detectar si Render está compilando otro estado del repositorio.
- Se revisaron los `.cs` para detectar uso de LINQ sin `using System.Linq;`; no quedan casos detectados.
- `CentralServerService.cs` tiene las llaves balanceadas y la estructura de clase/métodos cerrada correctamente en esta versión.

## Render
El servicio debe usar el `Dockerfile` incluido en la raíz del proyecto. Al comenzar el build debe aparecer la línea `sha256sum` para `CentralServerService.cs`. Si Render muestra líneas de código distintas de esta versión, el repositorio desplegado no coincide con este ZIP.

## Login Windows
Cuenta central → `YA TENGO CUENTA · INICIAR SESIÓN` → ventana independiente → correo + contraseña → Windows adopta el Store ID de la cuenta y entra al panel.

## Código alternativo
Seller Center → Dispositivos / QR → generar código → pegar en Windows. Es un método alternativo de vinculación del dispositivo, no el método principal de acceso.
