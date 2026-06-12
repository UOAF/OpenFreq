# OpenFreq Server
#
# Prebuilt images (published by CI, see .github/workflows/nightly.yml and release.yml):
#   ghcr.io/uoaf/openfreq-server:latest    - latest stable release
#   ghcr.io/uoaf/openfreq-server:<x.y.z>   - specific release
#   ghcr.io/uoaf/openfreq-server:nightly   - latest develop build (replaced on every push)
#
# Build from the repository root (submodules must be checked out first):
#   git submodule update --init --recursive
#   docker build -t openfreq-server -f OpenFreq.Server/Dockerfile .
#
# Run:
#   docker run -d -p 9987:9987 -p 9988:9988/udp \
#     -v ./OpenFreq.Server.json:/app/OpenFreq.Server.json ghcr.io/uoaf/openfreq-server
#
# Configuration is read from /app/OpenFreq.Server.json (a default is created on first start if none is mounted). 
# Logs are written to /app/logs.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore first so dependency layers cache across source changes
COPY OpenFreq.Server/OpenFreq.Server.csproj OpenFreq.Server/
COPY OpenFreq.Common/OpenFreq.Common.csproj OpenFreq.Common/
COPY libs/OpenFreqAudio/OpenFreqAudio/OpenFreqAudio.csproj libs/OpenFreqAudio/OpenFreqAudio/
RUN dotnet restore OpenFreq.Server/OpenFreq.Server.csproj

COPY OpenFreq.Server/ OpenFreq.Server/
COPY OpenFreq.Common/ OpenFreq.Common/
COPY libs/OpenFreqAudio/OpenFreqAudio/ libs/OpenFreqAudio/OpenFreqAudio/

# Version used for the client/server compatibility check; CI passes the
# nightly or release version so the image matches the client builds
ARG VERSION=0.0.0-dev
RUN dotnet publish OpenFreq.Server/OpenFreq.Server.csproj -c Release --no-restore \
    -p:InformationalVersion=$VERSION -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app/publish .

# Config file and logs/ are created next to the executable; the app must own /app
RUN chown -R $APP_UID /app
USER $APP_UID

# WebSocket signaling (TCP) and RTP audio (UDP)
EXPOSE 9987/tcp
EXPOSE 9988/udp

# Ports come from OpenFreq.Server.json; clear the base image's Kestrel default
ENV ASPNETCORE_HTTP_PORTS=

VOLUME /app/logs

ENTRYPOINT ["./OpenFreq.Server", "--headless"]
