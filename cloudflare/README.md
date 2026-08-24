# NexoMarket + Cloudflare R2

El servidor central de NexoMarket 4.2 es .NET 8 y se despliega como Web Service (Docker). Cloudflare R2 se usa como almacenamiento persistente de imágenes y copias de datos.

No se debe desplegar este repositorio directamente con `npx wrangler deploy`: ese comando espera un Worker/Assets y este proyecto es un servidor .NET.

La configuración de Render está en `render.yaml` y usa la instancia `free` para evitar el plan Starter.
