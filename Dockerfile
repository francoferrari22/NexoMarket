# NexoMarket Central Server 5.0.3 - Render / Docker
# Build separado para que Render muestre el error real de compilación si existiera.
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY NexoMarket.CentralServer/NexoMarket.CentralServer.csproj ./NexoMarket.CentralServer/
RUN dotnet restore ./NexoMarket.CentralServer/NexoMarket.CentralServer.csproj --verbosity minimal

COPY NexoMarket.CentralServer/ ./NexoMarket.CentralServer/
RUN dotnet build ./NexoMarket.CentralServer/NexoMarket.CentralServer.csproj -c Release --no-restore --verbosity normal
RUN dotnet publish ./NexoMarket.CentralServer/NexoMarket.CentralServer.csproj -c Release --no-build -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/runtime:8.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish ./
ENV PORT=8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "NexoMarket.CentralServer.dll"]
