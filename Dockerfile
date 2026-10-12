# Build
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /source

# Restore first so the package layer is cached until the project file changes.
COPY src/AtmCashManagement.Api/AtmCashManagement.Api.csproj src/AtmCashManagement.Api/
RUN dotnet restore src/AtmCashManagement.Api/AtmCashManagement.Api.csproj

COPY src/ src/
RUN dotnet publish src/AtmCashManagement.Api/AtmCashManagement.Api.csproj -c Release -o /app --no-restore

# Run
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
COPY --from=build /app .

ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080

# The base image ships a non-root user; the API needs no elevated rights.
USER $APP_UID
ENTRYPOINT ["dotnet", "AtmCashManagement.Api.dll"]
