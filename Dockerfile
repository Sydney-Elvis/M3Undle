# syntax=docker/dockerfile:1

ARG DOTNET_VERSION=10.0

FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION} AS build
WORKDIR /src

COPY global.json ./
COPY src/M3Undle.Web/M3Undle.Web.csproj src/M3Undle.Web/
COPY src/M3Undle.Core/M3Undle.Core.csproj src/M3Undle.Core/
RUN dotnet restore src/M3Undle.Web/M3Undle.Web.csproj

COPY src/ src/
COPY branding/ branding/
RUN dotnet publish src/M3Undle.Web/M3Undle.Web.csproj -c Release -o /app/publish --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_VERSION} AS final
WORKDIR /app

RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*

COPY --from=build /app/publish ./
RUN mkdir -p /app/Data \
    && chown -R app:app /app

USER app

ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_HTTP_PORTS=8080

VOLUME ["/app/Data"]
EXPOSE 8080

HEALTHCHECK --interval=30s --timeout=5s --start-period=30s --retries=3 \
    CMD curl --fail --silent http://127.0.0.1:8080/health || exit 1

ENTRYPOINT ["dotnet", "M3Undle.Web.dll"]
