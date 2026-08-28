# NexoMarket Central Server 5.4.0 - Render / Docker
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY NexoMarket.CentralServer/NexoMarket.CentralServer.csproj ./NexoMarket.CentralServer/
RUN dotnet restore ./NexoMarket.CentralServer/NexoMarket.CentralServer.csproj --verbosity minimal

COPY NexoMarket.CentralServer/ ./NexoMarket.CentralServer/
RUN echo "=== NEXOMARKET BUILD 5.23.4 ===" && \
    echo "Source: CentralServerService.cs" && \
    wc -l ./NexoMarket.CentralServer/CentralServerService.cs && \
    echo "=== COMPILANDO ===" && \
    dotnet build ./NexoMarket.CentralServer/NexoMarket.CentralServer.csproj -c Release --no-restore --verbosity minimal

RUN echo "=== PUBLICANDO ===" && \
    dotnet publish ./NexoMarket.CentralServer/NexoMarket.CentralServer.csproj -c Release --no-build -o /app/publish --no-restore --verbosity minimal

FROM mcr.microsoft.com/dotnet/runtime:8.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish ./
ENV PORT=8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "NexoMarket.CentralServer.dll"]
