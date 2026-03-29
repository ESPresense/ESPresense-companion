# Build stage
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build-env
ARG TARGETPLATFORM
ARG BUILDPLATFORM
ARG TARGETOS
ARG TARGETARCH

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
COPY ESPresense-companion.sln ./
COPY src/ESPresense.Companion.csproj src/
COPY src/ui/package.json src/ui/pnpm-lock.yaml src/ui/

RUN echo "Building on ${BUILDPLATFORM} for ${TARGETPLATFORM}" && \
    echo "TARGETOS=${TARGETOS}" && \
    echo "TARGETARCH=${TARGETARCH}" && \
    echo "TARGETPLATFORM=${TARGETPLATFORM}" && \
    echo "BUILDPLATFORM=${BUILDPLATFORM}"
RUN case "${TARGETARCH}" in \
        amd64) dotnet_arch=x64 ;; \
        arm64) dotnet_arch=arm64 ;; \
        arm) dotnet_arch=arm ;; \
        *) echo "Unsupported TARGETARCH=${TARGETARCH}" >&2; exit 1 ;; \
    esac && \
    dotnet restore src/ESPresense.Companion.csproj -r "${TARGETOS}-${dotnet_arch}"

COPY . ./

RUN case "${TARGETARCH}" in \
        amd64) dotnet_arch=x64 ;; \
        arm64) dotnet_arch=arm64 ;; \
        arm) dotnet_arch=arm ;; \
        *) echo "Unsupported TARGETARCH=${TARGETARCH}" >&2; exit 1 ;; \
    esac && \
    dotnet publish src/ESPresense.Companion.csproj -c Release --no-restore -r "${TARGETOS}-${dotnet_arch}" -o out

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /App
EXPOSE 8267 8268

ENV HTTP_PORTS=8267 \
    HTTPS_PORTS="" \
    OTA_UPDATE_PORT=8268 \
    CONFIG_DIR="/config/espresense"
ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=true

COPY --from=build-env /App/out .

LABEL \
  io.hass.version="VERSION" \
  io.hass.type="addon" \
  io.hass.arch="${TARGETPLATFORM}"

HEALTHCHECK --interval=30s --timeout=10s --start-period=5s --retries=3 \
  CMD curl -f http://localhost:8267/healthz || exit 1

ENTRYPOINT ["dotnet", "ESPresense.Companion.dll"]
