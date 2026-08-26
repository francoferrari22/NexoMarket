NexoMarket 5.2.3 - CORREGIDO FINAL

Correcciones de esta entrega:
1) CentralServerService.cs: corregidos los literales C# que provocaban CS1012, CS1003 y CS1009 en las lineas reportadas por Render.
2) Ticket web: corregido el patron Regex de ItemsJson para que compile correctamente.
3) Comprobante de pago: corregida la vista previa HTML de la imagen para evitar comillas sin escapar dentro del literal C#.
4) R2: se conserva la configuracion DisablePayloadSigning=true y DisableDefaultChecksumValidation=true para Cloudflare R2.
5) Las imagenes se siguen sirviendo por /media/... desde el servidor central, leyendo desde R2.
6) Windows: login NexoMarket reorganizado con marca NEXO en verde fluo y MARKET en blanco, con presentacion mas limpia.
7) SellerAccountForm y SellerSignInForm tambien usan el mismo tratamiento visual de marca.
8) No se modificaron las conexiones centrales ni la sincronizacion existente.

Validacion local:
- Integridad del ZIP verificada.
- Analisis lexicografico C# de todos los .cs sin literales de cadena/caracter sin cerrar.
- Balance de llaves, parentesis y corchetes fuera de strings/comentarios verificado en todos los .cs.

Nota: este entorno no dispone del SDK .NET, por lo que no se declara una compilacion real con dotnet build aqui.
