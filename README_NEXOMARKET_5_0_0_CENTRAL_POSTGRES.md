# NexoMarket 5.0.0 — Central PostgreSQL

## Objetivo

Windows y Web dejan de ser dos bases independientes. Ambos clientes usan el mismo Central y el mismo StoreId. PostgreSQL es la fuente de verdad; XML/R2 quedan como respaldo y migración de instalaciones anteriores.

## Arquitectura

```
NexoMarket Central (Render)
        |
   PostgreSQL
        |
  +-----+-----+
  |           |
 Windows     Web
 Seller      Seller Center
```

La sincronización de Windows continúa con el ciclo de 20 segundos, pero ya no copia una base Windows hacia una base Web. Consulta cambios del mismo estado central mediante `/api/sync/delta`.

## Render: configuración obligatoria

1. En Render crear o reutilizar un PostgreSQL para NexoMarket.
2. En el servicio `nexomarket-022`, abrir **Environment**.
3. Crear `NEXOMARKET_DATABASE_URL` con el **Internal Connection String** de ese PostgreSQL. No usar la URL externa.
4. Guardar y desplegar.
5. Abrir `https://nexomarket-022.onrender.com/api/central/status`. Debe responder algo similar a `OK|database=connected|documents=4|r2=...`.

Render permite conectar un servicio con PostgreSQL mediante `fromDatabase`/connection string; para servicios en Render se recomienda la conexión interna.

## Migración

Al primer arranque con PostgreSQL vacío, el servidor importa automáticamente los XML existentes (o el respaldo R2 si está disponible) y los coloca en PostgreSQL. A partir de ese momento PostgreSQL es la fuente principal.

## Regla de datos

- `StoreId` identifica una única tienda.
- `Account` pertenece a una tienda.
- Productos, inventario, pedidos, clientes y configuración pertenecen a `StoreId`.
- Windows no publica una copia completa de su base local para reemplazar la Web.
- Web y Windows escriben en el Central y leen del mismo Central.
- Los 20 segundos son un ciclo de actualización/reintento; no son una copia entre dos bases.

## Seguridad

`StoreId` identifica la tienda, pero no debería ser la única credencial de acceso a largo plazo. El siguiente paso recomendado es vincular cada instalación Windows mediante cuenta + contraseña o QR y un `DeviceId`, manteniendo StoreId como identidad de tienda.
