# Build stage
FROM mcr.microsoft.com/dotnet/sdk:8.0-bookworm-slim-amd64 AS build-env
ARG TARGETPLATFORM
ARG BUILDPLATFORM

# Install Node.js
RUN apt-get update && apt-get install -y ca-certificates curl gnupg && \
    mkdir -p /etc/apt/keyrings && \
    curl -fsSL https://deb.nodesource.com/gpgkey/nodesource-repo.gpg.key | gpg --dearmor -o /etc/apt/keyrings/nodesource.gpg && \
    echo "deb [signed-by=/etc/apt/keyrings/nodesource.gpg] https://deb.nodesource.com/node_20.x nodistro main" | tee /etc/apt/sources.list.d/nodesource.list && \
    apt-get update && \
    apt-get install -y nodejs && \
    apt-get clean && \
    rm -rf /var/lib/apt/lists/*

WORKDIR /App
COPY . ./

RUN echo "Building on ${BUILDPLATFORM} for ${TARGETPLATFORM}"

RUN dotnet add src/ESPresense.Companion.csproj package MathNet.Numerics.Providers.MKL
RUN dotnet restore
RUN dotnet publish -c Release -o out

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /App
EXPOSE 8267 8268

ENV ASPNETCORE_URLS="http://+:8267" \
    OTA_UPDATE_PORT=8268 \
    CONFIG_DIR="/config/espresense"

RUN if [ "${TARGETPLATFORM}" = "linux/amd64" ]; then \
      apt-get update && apt-get install -y apt-transport-https gnupg software-properties-common wget && \
      wget -qO - https://apt.repos.intel.com/intel-gpg-keys/GPG-PUB-KEY-INTEL-SW-PRODUCTS.PUB | gpg --dearmor -o /usr/share/keyrings/intel-software.gpg && \
      echo "deb [signed-by=/usr/share/keyrings/intel-software.gpg] https://apt.repos.intel.com/mkl all main" > /etc/apt/sources.list.d/intel-mkl.list && \
      apt-get update && apt-get install -y intel-mkl-64bit-2020.0-088; \
    fi

COPY --from=build-env /App/out .

LABEL \
  io.hass.version="VERSION" \
  io.hass.type="addon" \
  io.hass.arch="${TARGETPLATFORM}"

ENTRYPOINT ["dotnet", "ESPresense.Companion.dll"]
