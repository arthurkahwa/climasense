# syntax=docker/dockerfile:1.7
#
# ClimaSense.Web — multi-stage build for net10.0.
#
# Stage 1: restore + publish against the SDK image.
# Stage 2: minimal runtime image. `curl` is included so docker-compose's
#          healthcheck (CMD-SHELL "curl -fsS http://localhost:8080/api/health/ready")
#          works without a fat shell.

ARG DOTNET_VERSION=10.0
ARG DOTNET_VARIANT=preview

FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION}-${DOTNET_VARIANT} AS build
WORKDIR /src

COPY ClimaSense.Web.csproj ./
RUN dotnet restore ClimaSense.Web.csproj

COPY . ./
RUN dotnet publish ClimaSense.Web.csproj \
    -c Release \
    -o /app/publish \
    --no-restore \
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_VERSION}-${DOTNET_VARIANT} AS runtime
WORKDIR /app

# Healthcheck dependency. `tini` provides PID 1 signal forwarding so
# `docker stop` is clean.
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl tini \
    && rm -rf /var/lib/apt/lists/*

COPY --from=build /app/publish ./

EXPOSE 8080

ENV ASPNETCORE_URLS=http://+:8080 \
    DOTNET_RUNNING_IN_CONTAINER=true \
    DOTNET_NOLOGO=1

ENTRYPOINT ["/usr/bin/tini", "--", "dotnet", "ClimaSense.Web.dll"]
