# NexoMarket 5.15.1 — FIX Render CS0120

## Error corregido
`CS0120: An object reference is required for the non-static field, method, or property 'CentralServerService.StoreRatingSummary(string)'`

### Causa
`CentralStore` es una clase anidada y su constructor `CentralStore(XElement e)` intentaba invocar `StoreRatingSummary(StoreId)`, que es un método de instancia de `CentralServerService`. Una clase anidada no dispone de una instancia del servidor para realizar esa llamada.

### Corrección segura
El constructor ya no intenta calcular la reputación desde el contexto incorrecto. Inicializa `RatingSummary` con `0.0|0`.

El flujo real del marketplace ya obtiene `RatingSummary` desde `StoreLines(...)`, donde el método de instancia sí se invoca correctamente, por lo que no se elimina la funcionalidad pública de reseñas.

## SYSLIB0041
Los avisos sobre `Rfc2898DeriveBytes` son warnings y no son la causa del `Build FAILED`. No se modificó el esquema de contraseñas en este parche para evitar invalidar hashes existentes. La migración a SHA-256 debe hacerse con estrategia de compatibilidad y migración progresiva, no en caliente.

## Verificación
Se comprobó que `CentralStore(XElement)` ya no contiene la llamada inválida y que no existen instancias `new CentralStore(XElement)` en el código actual.

El entorno de trabajo disponible no tiene instalado el SDK `dotnet`, por lo que no se afirma una compilación local completa. Render debe ejecutar el build final.
