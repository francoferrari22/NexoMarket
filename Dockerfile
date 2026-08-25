# NexoMarket Central Server 5.1.2 - Render / Docker
# Esta versión corrige un HTML fuera de una cadena C# que provocaba CS0106/CS1022.
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY NexoMarket.CentralServer/NexoMarket.CentralServer.csproj ./NexoMarket.CentralServer/
RUN dotnet restore ./NexoMarket.CentralServer/NexoMarket.CentralServer.csproj --verbosity minimal

COPY NexoMarket.CentralServer/ ./NexoMarket.CentralServer/
RUN echo '--- NEXOMARKET SOURCE CHECK ---' && \
    sha256sum ./NexoMarket.CentralServer/CentralServerService.cs ./NexoMarket.CentralServer/CentralDatabase.cs ./NexoMarket.CentralServer/Program.cs && \
    wc -l ./NexoMarket.CentralServer/CentralServerService.cs && \
    echo '--- SOURCE TAIL ---' && tail -n 12 ./NexoMarket.CentralServer/CentralServerService.cs
RUN dotnet build ./NexoMarket.CentralServer/NexoMarket.CentralServer.csproj -c Release --no-restore --verbosity normal
RUN dotnet publish ./NexoMarket.CentralServer/NexoMarket.CentralServer.csproj -c Release --no-build -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/runtime:8.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish ./
ENV PORT=8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "NexoMarket.CentralServer.dll"]
