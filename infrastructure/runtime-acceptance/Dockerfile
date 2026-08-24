# syntax=docker/dockerfile:1.7
ARG SDK_IMAGE=mcr.microsoft.com/dotnet/sdk:10.0.202-noble
ARG RUNTIME_IMAGE=mcr.microsoft.com/dotnet/aspnet:10.0.4-noble

FROM ${SDK_IMAGE} AS build
WORKDIR /tmp
COPY Directory.Build.props /source/
COPY shared/gameplay/ /source/shared/gameplay/
COPY shared/generated/ /source/shared/generated/
COPY shared/protocol/ /source/shared/protocol/
COPY shared/realtime/ /source/shared/realtime/
COPY server/vendor/Fantasy/nupkg/ /source/server/vendor/Fantasy/nupkg/
COPY server/src/Modules/AiNative.Server.Fantasy/ /source/server/src/Modules/AiNative.Server.Fantasy/
COPY server/src/Modules/AiNative.Server.Protocol/ /source/server/src/Modules/AiNative.Server.Protocol/
COPY server/src/Hosts/AiNative.BattleHost/ /source/server/src/Hosts/AiNative.BattleHost/
RUN dotnet restore /source/server/src/Hosts/AiNative.BattleHost/AiNative.BattleHost.csproj \
    && dotnet publish /source/server/src/Hosts/AiNative.BattleHost/AiNative.BattleHost.csproj \
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
      org.ainative.protocol.identity="${PROTOCOL_IDENTITY}" \
      org.ainative.release-status="acceptance-candidate"
WORKDIR /app
COPY --from=build --chown=$APP_UID:$APP_UID /artifacts/host/ ./
ENV ASPNETCORE_URLS=http://+:8080
ENV AINATIVE_SOURCE_COMMIT=${SOURCE_COMMIT} \
    AINATIVE_FANTASY_COMMIT=${FANTASY_COMMIT} \
    AINATIVE_PROTOCOL_IDENTITY=${PROTOCOL_IDENTITY}
EXPOSE 8080
EXPOSE 22000/udp
USER $APP_UID
ENTRYPOINT ["dotnet", "AiNative.BattleHost.dll", "-m", "Develop"]
