# syntax=docker/dockerfile:1.7
ARG SDK_IMAGE=mcr.microsoft.com/dotnet/sdk:10.0.202-noble
ARG RUNTIME_IMAGE=mcr.microsoft.com/dotnet/runtime:10.0.4-noble

FROM ${SDK_IMAGE} AS build
WORKDIR /source
COPY server/vendor/Fantasy/ ./server/vendor/Fantasy/
RUN dotnet restore server/vendor/Fantasy/examples/Server/Server.sln \
    && dotnet publish server/vendor/Fantasy/examples/Server/APP/Main/Main.csproj \
        --configuration Release \
        --framework net10.0 \
        --no-restore \
        --output /artifacts/host \
        --nologo

FROM ${RUNTIME_IMAGE} AS runtime
ARG SOURCE_COMMIT=unknown
ARG FANTASY_COMMIT=unknown
ARG PROTOCOL_IDENTITY=none
LABEL org.opencontainers.image.revision="${SOURCE_COMMIT}" \
      org.ainative.fantasy.revision="${FANTASY_COMMIT}" \
      org.ainative.protocol.identity="${PROTOCOL_IDENTITY}"
WORKDIR /app
COPY --from=build --chown=$APP_UID:$APP_UID /artifacts/host/ ./
USER root
RUN mkdir -p /Logs && chown -R $APP_UID:$APP_UID /Logs /app
USER $APP_UID
ENTRYPOINT ["dotnet", "Main.dll", "-m", "Develop"]
