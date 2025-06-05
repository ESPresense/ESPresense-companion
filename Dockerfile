# Build stage
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:8.0 AS build-env
ARG TARGETPLATFORM
ARG BUILDPLATFORM

# Install Node.js
RUN apt-get update && apt-get install -y ca-certificates curl gnupg && \
    mkdir -p /etc/apt/keyrings && \
    curl -fsSL https://deb.nodesource.com/gpgkey/nodesource-repo.gpg.key | gpg --dearmor -o /etc/apt/keyrings/nodesource.gpg && \
    echo "deb [signed-by=/etc/apt/keyrings/nodesource.gpg] https://deb.nodesource.com/node_20.x nodistro main" | tee /etc/apt/sources.list.d/nodesource.list && \
    apt-get update && \
    apt-get install -y nodejs && \
    npm install -g pnpm && \
    apt-get clean && \
    rm -rf /var/lib/apt/lists/*

WORKDIR /App
COPY . ./

RUN echo "Building on ${BUILDPLATFORM} for ${TARGETPLATFORM}" && \
    echo "TARGETPLATFORM=${TARGETPLATFORM}" && \
    echo "BUILDPLATFORM=${BUILDPLATFORM}"
RUN dotnet restore
RUN dotnet publish -c Release -o out

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /App
EXPOSE 8267 8268

ENV HTTP_PORTS=8267 \
    HTTPS_PORTS="" \
    OTA_UPDATE_PORT=8268 \
    CONFIG_DIR="/config/espresense"

COPY --from=build-env /App/out .

LABEL \
  io.hass.version="VERSION" \
  io.hass.type="addon" \
  io.hass.arch="${TARGETPLATFORM}"

ENTRYPOINT ["dotnet", "ESPresense.Companion.dll"]
