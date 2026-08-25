NexoMarket 4.1.15 - FIX DEPLOY RENDER

Cambios:
1. Se actualizó AWSSDK.S3 de 3.7.400.17 a 3.7.402.8. La versión 3.7.402.8 está publicada en NuGet y es compatible con net8.0.
2. Dockerfile ya no usa --no-restore en dotnet publish. Esto permite que Render restaure dependencias de forma limpia durante publish y evita usar un assets file/cache incompatible.
3. Se mantiene .NET 8, R2 y todos los endpoints existentes.

Deploy:
- Reemplazar el contenido del repositorio por esta versión.
- Commit/push.
- Render: Manual Deploy -> Deploy latest commit.
- Si vuelve a fallar, abrir All Logs y buscar la primera línea que empiece por error CS o error NU. Esa línea es la causa real; el mensaje "exit code 1" solo es el resumen.
