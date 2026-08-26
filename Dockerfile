# NexoMarket Central Server 5.12.0 - Render / Docker
# Render compila dentro del contenedor; no requiere MSBuild instalado en la máquina del usuario.
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY NexoMarket.CentralServer/NexoMarket.CentralServer.csproj ./NexoMarket.CentralServer/
RUN dotnet restore ./NexoMarket.CentralServer/NexoMarket.CentralServer.csproj --verbosity minimal

COPY NexoMarket.CentralServer/ ./NexoMarket.CentralServer/

# Un único publish: compila y publica en Release y evita ejecutar un build redundante.
RUN echo "=== NEXOMARKET BUILD 5.12.0 ===" && \
    echo "=== SOURCE CHECK ===" && \
    wc -l ./NexoMarket.CentralServer/CentralServerService.cs && \
    dotnet publish ./NexoMarket.CentralServer/NexoMarket.CentralServer.csproj \
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
