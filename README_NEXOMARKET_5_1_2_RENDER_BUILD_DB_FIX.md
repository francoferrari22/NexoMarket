# NexoMarket 5.1.2 — Render build + PostgreSQL diagnostics

## Corrección crítica
Se encontró la causa real del CS0106/CS1022 de Render en `CentralServerService.cs`: había HTML crudo después de un `;` fuera de una cadena C# en la sección del carrito (`b.Append(...);<aside ...`). Eso hacía que el compilador interpretara el resto del archivo fuera del método y generara errores en cascada hasta el final del archivo.

Se corrigió la construcción para que todo el HTML siga dentro de la cadena/Append correspondiente.

## Diagnóstico de Render
El Dockerfile imprime SHA256 y cantidad de líneas de los tres fuentes críticos antes de compilar. Si Render muestra otro hash, está desplegando otro commit/estado del repositorio.

Al iniciar, el servidor imprime `DB status:` sin exponer la contraseña.

## PostgreSQL
El servidor acepta `NEXOMARKET_DATABASE_URL` y, como respaldo, `DATABASE_URL`.
La conexión de Render debe ser la del PostgreSQL existente. No crear una base nueva si la cuenta/tienda ya existe.

Después de un deploy exitoso, abrir:
`/health`
`/api/central/status`

El segundo debe informar `database=connected|documents=...`.

## Orden de prueba
1. Deploy con Clear build cache.
2. Confirmar `Build successful`.
3. Abrir `/health`.
4. Abrir `/api/central/status` y confirmar PostgreSQL conectado.
5. Recién después probar crear/iniciar sesión en Windows.
