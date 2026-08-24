# NexoMarket Central 4.0 - servidor público permanente
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY NexoMarket.CentralServer/NexoMarket.CentralServer.csproj NexoMarket.CentralServer/
RUN dotnet restore NexoMarket.CentralServer/NexoMarket.CentralServer.csproj
COPY NexoMarket.CentralServer/ NexoMarket.CentralServer/
RUN dotnet publish NexoMarket.CentralServer/NexoMarket.CentralServer.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/runtime:8.0
WORKDIR /app
COPY --from=build /app/publish .
ENV PORT=10000
EXPOSE 10000
ENTRYPOINT ["dotnet", "NexoMarket.CentralServer.dll"]
