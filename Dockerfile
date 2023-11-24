FROM mcr.microsoft.com/dotnet/sdk:8.0 as build-env
ARG TARGETPLATFORM
ARG BUILDPLATFORM

RUN curl -fsSL https://deb.nodesource.com/setup_19.x | bash - && apt-get install -y nodejs

WORKDIR /App

COPY . ./
RUN dotnet restore

RUN echo "I am running on ${BUILDPLATFORM}"
RUN echo "building for ${TARGETPLATFORM}"
RUN export TARGETPLATFORM="${TARGETPLATFORM}"

RUN dotnet publish -c Release -o out

FROM mcr.microsoft.com/dotnet/aspnet:8.0-alpine
WORKDIR /App
EXPOSE 8267 8268
ENV ASPNETCORE_URLS "http://+:8267"
ENV OTA_UPDATE_PORT 8268
ENV CONFIG_DIR "/config/espresense"
ENV MathNetNumericsLAProvider=MKL
COPY --from=build-env /App/out .
LABEL \
    io.hass.version="VERSION"
ENTRYPOINT ["dotnet", "ESPresense.Companion.dll"]
