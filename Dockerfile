# NexoMarket Central Server 5.12.3 RENDER BUILD CLEAN - Render / Docker
# IMPORTANTE: este Dockerfile compila exclusivamente el CentralServer incluido en este paquete.
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY NexoMarket.CentralServer/NexoMarket.CentralServer.csproj ./NexoMarket.CentralServer/
RUN dotnet restore ./NexoMarket.CentralServer/NexoMarket.CentralServer.csproj --verbosity minimal

COPY NexoMarket.CentralServer/ ./NexoMarket.CentralServer/

# Huella del fuente: si Render está usando otro commit/archivo, queda visible inmediatamente
# en los logs y el build no continúa con una versión equivocada.
RUN echo "=== NEXOMARKET 5.12.3 RENDER BUILD CLEAN / SOURCE CHECK ===" && \
    wc -l ./NexoMarket.CentralServer/CentralServerService.cs && \
    sha256sum ./NexoMarket.CentralServer/CentralServerService.cs && \
    echo "PlatformFeeForStore definition:" && \
    grep -n "private string PlatformFeeForStore" ./NexoMarket.CentralServer/CentralServerService.cs && \
    test "$(grep -c "private string PlatformFeeForStore" ./NexoMarket.CentralServer/CentralServerService.cs)" = "1"

# Único paso de compilación/publicación. No hay código C# generado ni parcheado durante el build.
RUN dotnet publish ./NexoMarket.CentralServer/NexoMarket.CentralServer.csproj \
      -c Release \
      --no-restore \
      -o /app/publish \
      --verbosity minimal

FROM mcr.microsoft.com/dotnet/runtime:8.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish ./
ENV PORT=8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "NexoMarket.CentralServer.dll"]
