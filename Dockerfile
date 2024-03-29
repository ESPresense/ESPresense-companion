FROM mcr.microsoft.com/dotnet/sdk:8.0-bookworm-slim-amd64 as build-env
ARG TARGETPLATFORM
ARG BUILDPLATFORM

RUN apt-get update \
  && apt-get install -y ca-certificates curl gnupg \
  && mkdir -p /etc/apt/keyrings \
  && curl -fsSL https://deb.nodesource.com/gpgkey/nodesource-repo.gpg.key | gpg --dearmor -o /etc/apt/keyrings/nodesource.gpg \
  && echo "deb [signed-by=/etc/apt/keyrings/nodesource.gpg] https://deb.nodesource.com/node_20.x nodistro main" | tee /etc/apt/sources.list.d/nodesource.list \
  && apt-get update \
  && apt-get install nodejs -y;

WORKDIR /App

COPY . ./
RUN dotnet restore

RUN echo "I am running on ${BUILDPLATFORM}"
RUN echo "building for ${TARGETPLATFORM}"
RUN export TARGETPLATFORM="${TARGETPLATFORM}"

RUN dotnet publish -c Release -o out

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /App
EXPOSE 8267 8268
ENV ASPNETCORE_URLS "http://+:8267"
ENV OTA_UPDATE_PORT 8268
ENV CONFIG_DIR "/config/espresense"
COPY --from=build-env /App/out .
LABEL \
  io.hass.version="VERSION" \
  io.hass.type="addon" \
  io.hass.arch="${TARGETPLATFORM}"

ENTRYPOINT ["dotnet", "ESPresense.Companion.dll"]
