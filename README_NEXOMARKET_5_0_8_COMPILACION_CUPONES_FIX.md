# NexoMarket 5.0.8 — compilación y cupones

Correcciones aplicadas sobre 5.0.7:

- Agregado `using System.Linq;` a `NexoMarket.Admin/UI/CentralSyncService.cs`, corrigiendo el `CS8161` de `FirstOrDefault()`.
- Reforzada la publicación de cupones en Central: se busca por `StoreId + CouponId` o por `StoreId + Code`, conservando el `CouponId` central cuando Windows tiene un ID local diferente. Esto evita duplicaciones al sincronizar.
- Reforzado el consumo del límite de usos: validación final del cupón, reserva de stock y aumento de `Used` se ejecutan bajo el mismo lock de Central, evitando que pedidos concurrentes superen `MaxUses`.
- Si la reserva de stock falla, el cupón no se consume.

Nota: en este entorno no está instalado el SDK/MSBuild de .NET Framework, por lo que no se pudo ejecutar una compilación real aquí. Se realizó revisión estática de los `.cs` y no se detectaron otros archivos que usen LINQ sin `System.Linq`.
