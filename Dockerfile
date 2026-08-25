# NexoMarket Central Server 4.1.16 - Render / Docker
# Build robusto: restaura dependencias despues de copiar el proyecto completo.
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copiar el proyecto y sus fuentes antes de restaurar para que el contexto
# de compilacion sea exactamente el mismo que usa publish.
COPY NexoMarket.CentralServer/ ./NexoMarket.CentralServer/

RUN dotnet restore ./NexoMarket.CentralServer/NexoMarket.CentralServer.csproj
RUN dotnet publish ./NexoMarket.CentralServer/NexoMarket.CentralServer.csproj \
    -c Release \
    -o /app/publish

FROM mcr.microsoft.com/dotnet/runtime:8.0 AS runtime
WORKDIR /app

COPY --from=build /app/publish ./

# Render entrega PORT automaticamente. El programa NexoMarket lo lee.
ENV PORT=8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "NexoMarket.CentralServer.dll"]
