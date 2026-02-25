# syntax=docker/dockerfile:1
# Integration test container that runs the CLI against real or sample providers
# and compares output against a baseline (legacy Python script).

ARG DOTNET_VERSION=10.0-preview

FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION} AS build

RUN apt-get update \
  && apt-get install -y --no-install-recommends python3 curl ca-certificates diffutils \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /workspace

# Copy solution and project files for restore
COPY M3Undle.sln ./
COPY src/M3Undle.Cli/M3Undle.Cli.csproj src/M3Undle.Cli/
COPY src/M3Undle.Core/M3Undle.Core.csproj src/M3Undle.Core/
COPY src/M3Undle.Web/M3Undle.Web.csproj src/M3Undle.Web/
COPY src/M3Undle.SocketHost/M3Undle.SocketHost.csproj src/M3Undle.SocketHost/

RUN dotnet restore M3Undle.sln

# Copy all source
COPY src/ src/

# Build the CLI project
RUN dotnet build src/M3Undle.Cli/M3Undle.Cli.csproj -c Release --no-restore

# Copy integration test assets
COPY tests/Integration/ /opt/integration/

WORKDIR /opt/integration

ENTRYPOINT ["bash", "/opt/integration/entrypoint.sh"]
