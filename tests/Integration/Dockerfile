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
COPY IPTVGuideDog.sln ./
COPY src/IPTVGuideDog.Cli/IPTVGuideDog.Cli.csproj src/IPTVGuideDog.Cli/
COPY src/IPTVGuideDog.Core/IPTVGuideDog.Core.csproj src/IPTVGuideDog.Core/
COPY src/IPTVGuideDog.Web/IPTVGuideDog.Web.csproj src/IPTVGuideDog.Web/
COPY src/IPTVGuideDog.SocketHost/IPTVGuideDog.SocketHost.csproj src/IPTVGuideDog.SocketHost/

RUN dotnet restore IPTVGuideDog.sln

# Copy all source
COPY src/ src/

# Build the CLI project
RUN dotnet build src/IPTVGuideDog.Cli/IPTVGuideDog.Cli.csproj -c Release --no-restore

# Copy integration test assets
COPY tests/Integration/ /opt/integration/

WORKDIR /opt/integration

ENTRYPOINT ["bash", "/opt/integration/entrypoint.sh"]
