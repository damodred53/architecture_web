FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS base
WORKDIR /app
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src

COPY ApiElasticSearch.csproj ./
RUN dotnet restore "ApiElasticSearch.csproj"

COPY . .
RUN dotnet restore

# Build (facultatif si publish direct)
RUN dotnet build "ApiElasticSearch.csproj" -c $BUILD_CONFIGURATION -o /app/build --no-restore

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
# R2R enlevé pour éviter les soucis de RID sur Apple Silicon; tu peux le remettre plus tard
RUN dotnet publish "ApiElasticSearch.csproj" -c $BUILD_CONFIGURATION -o /app/publish \
    --no-restore \
    /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish ./

COPY .ledico-d498c-firebase-adminsdk-fbsvc-eef0abcf49.json ./
ENV GOOGLE_APPLICATION_CREDENTIALS=/app/.ledico-d498c-firebase-adminsdk-fbsvc-eef0abcf49.json

ENTRYPOINT ["dotnet", "ApiElasticSearch.dll"]