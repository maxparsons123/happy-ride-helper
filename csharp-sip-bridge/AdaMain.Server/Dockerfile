FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copy project files
COPY AdaMain/AdaMain.csproj AdaMain/
COPY AdaMain.Server/AdaMain.Server.csproj AdaMain.Server/
RUN dotnet restore AdaMain.Server/AdaMain.Server.csproj

# Copy source and publish
COPY AdaMain/ AdaMain/
COPY AdaMain.Server/ AdaMain.Server/
RUN dotnet publish AdaMain.Server/AdaMain.Server.csproj \
    -c Release \
    -o /app \
    --no-restore

# Runtime image (slim)
FROM mcr.microsoft.com/dotnet/runtime:8.0-alpine AS runtime
WORKDIR /app
COPY --from=build /app .

# Default config (override with env vars or mounted appsettings.json)
EXPOSE 5060/udp
EXPOSE 10000-10100/udp

ENTRYPOINT ["dotnet", "AdaMain.Server.dll"]
