# syntax=docker/dockerfile:1

FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build

ARG TARGETARCH
ARG BUILD_CONFIGURATION=Release

WORKDIR /source

COPY --link .editorconfig Directory.Build.props global.json ./
COPY --link src/GoldSrcOps.Api/GoldSrcOps.Api.csproj src/GoldSrcOps.Api/
COPY --link src/GoldSrcOps.Application/GoldSrcOps.Application.csproj src/GoldSrcOps.Application/
COPY --link src/GoldSrcOps.Contracts/GoldSrcOps.Contracts.csproj src/GoldSrcOps.Contracts/
COPY --link src/GoldSrcOps.Domain/GoldSrcOps.Domain.csproj src/GoldSrcOps.Domain/
COPY --link src/GoldSrcOps.Infrastructure/GoldSrcOps.Infrastructure.csproj src/GoldSrcOps.Infrastructure/

RUN dotnet restore src/GoldSrcOps.Api/GoldSrcOps.Api.csproj -a $TARGETARCH

COPY --link src/ src/

RUN dotnet publish src/GoldSrcOps.Api/GoldSrcOps.Api.csproj \
    -a $TARGETARCH \
    -c $BUILD_CONFIGURATION \
    --no-restore \
    -o /app/publish \
    /p:UseAppHost=false \
    && rm -f /app/publish/appsettings.Development.json /app/publish/appsettings.Local.json

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime

ENV ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_HOSTBUILDER__RELOADCONFIGONCHANGE=false

EXPOSE 8080

WORKDIR /app
COPY --link --from=build /app/publish .

USER $APP_UID

ENTRYPOINT ["dotnet", "GoldSrcOps.Api.dll"]
