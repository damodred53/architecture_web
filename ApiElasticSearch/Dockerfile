# -------- RUNTIME (base) --------
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS base
WORKDIR /app

# Var d'env: écoute sur 8080
ENV ASPNETCORE_URLS=http://+:8080 \
    DOTNET_EnableDiagnostics=0 \
    DOTNET_gcServer=1 \
    DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1

# Crée un user non-root (UID configurable)
ARG APP_UID=10001
RUN useradd --uid ${APP_UID} --create-home --shell /sbin/nologin appuser \
    && chown -R appuser:appuser /app
USER appuser

EXPOSE 8080

# -------- BUILD (sdk) --------
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src

# Copie fine pour profiter du cache
# (ajoute ici *.sln et éventuellement Directory.Packages.props si tu l’utilises)
COPY ApiElasticSearch/ApiElasticSearch.csproj ApiElasticSearch/
RUN dotnet restore "ApiElasticSearch/ApiElasticSearch.csproj"

# Copie du reste du code
COPY . .
WORKDIR /src/ApiElasticSearch
RUN dotnet build "ApiElasticSearch.csproj" -c $BUILD_CONFIGURATION -o /app/build --no-restore

# -------- PUBLISH --------
FROM build AS publish
ARG BUILD_CONFIGURATION=Release
WORKDIR /src/ApiElasticSearch
RUN dotnet publish "ApiElasticSearch.csproj" -c $BUILD_CONFIGURATION -o /app/publish \
    --no-build \
    /p:UseAppHost=false \
    /p:PublishReadyToRun=true

# -------- FINAL --------
FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish ./
ENTRYPOINT ["dotnet", "ApiElasticSearch.dll"]