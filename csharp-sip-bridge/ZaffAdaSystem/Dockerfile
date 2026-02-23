FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY AdaSdkModel/AdaSdkModel.csproj AdaSdkModel/
RUN dotnet restore AdaSdkModel/AdaSdkModel.csproj

COPY AdaSdkModel/ AdaSdkModel/
RUN dotnet publish AdaSdkModel/AdaSdkModel.csproj \
    -c Release \
    -o /app \
    --no-restore

FROM mcr.microsoft.com/dotnet/runtime:8.0-alpine AS runtime
WORKDIR /app
COPY --from=build /app .

EXPOSE 5060/udp
EXPOSE 10000-10100/udp

ENTRYPOINT ["dotnet", "AdaSdkModel.dll"]
