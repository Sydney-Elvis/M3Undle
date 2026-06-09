# syntax=docker/dockerfile:1

ARG DOTNET_VERSION=10.0

FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION} AS build
WORKDIR /src

ARG BUILD_NUMBER=
ARG BUILD_DATE_UTC=
ARG SOURCE_REVISION=

COPY global.json ./
COPY src/M3Undle.Web/M3Undle.Web.csproj src/M3Undle.Web/
COPY src/M3Undle.Core/M3Undle.Core.csproj src/M3Undle.Core/
RUN dotnet restore src/M3Undle.Web/M3Undle.Web.csproj

COPY src/ src/
COPY branding/ branding/
RUN BUILD_NUMBER_ARG=""; \
    BUILD_DATE_ARG=""; \
    SOURCE_REVISION_ARG=""; \
    if [ -n "$BUILD_NUMBER" ]; then BUILD_NUMBER_ARG="/p:BuildNumber=$BUILD_NUMBER"; fi; \
    if [ -n "$BUILD_DATE_UTC" ]; then BUILD_DATE_ARG="/p:BuildDateUtc=$BUILD_DATE_UTC"; fi; \
    if [ -n "$SOURCE_REVISION" ]; then SOURCE_REVISION_ARG="/p:SourceRevisionId=$SOURCE_REVISION"; fi; \
    dotnet publish src/M3Undle.Web/M3Undle.Web.csproj -c Release -o /app/publish /p:UseAppHost=false $BUILD_NUMBER_ARG $BUILD_DATE_ARG $SOURCE_REVISION_ARG

FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_VERSION} AS final
WORKDIR /app

RUN apt-get update \
    && apt-get install -y --no-install-recommends curl ffmpeg \
    && rm -rf /var/lib/apt/lists/*

COPY --from=build /app/publish ./

RUN mkdir -p /data /config /data/hls-work

ENV ASPNETCORE_URLS=http://+:5004;http://+:8080 \
    ASPNETCORE_HTTP_PORTS=5004;8080 \
    HOME=/data \
    M3UNDLE_CONFIG_DIR=/config \
    M3UNDLE_M3U_DIR=/m3u_data

VOLUME ["/data", "/config"]
EXPOSE 5004 8080

HEALTHCHECK --interval=30s --timeout=5s --start-period=30s --retries=3 \
    CMD curl --fail --silent http://127.0.0.1:8080/livez || exit 1

ENTRYPOINT ["dotnet", "M3Undle.Web.dll"]
