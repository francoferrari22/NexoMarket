NexoMarket 4.1.11 - FIX COMPILACION

Corrección del error mostrado en LicenseGateForm.cs línea 25:
El evento Button.Click es un evento de .NET y debe conectarse con +=, no con =.
Se corrigió:
    close.Click=delegate{ ... };
por:
    close.Click+=delegate{ ... };

Se conserva el sistema de licencia por CUENTA:
- Vendedor: prueba inicial de 60 días por cuenta.
- Comprador: sin licencia.
- Machine ID no determina la duración de la licencia.
- Los códigos/token se validan contra la cuenta.

No se modificó la lógica de productos, ventas ni sincronización fuera de lo necesario.
