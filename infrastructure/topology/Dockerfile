# syntax=docker/dockerfile:1
ARG SDK_VERSION=10.0.202
FROM mcr.microsoft.com/dotnet/sdk:${SDK_VERSION} AS build
ARG HOST=GateHost
WORKDIR /source
COPY global.json Directory.Build.props ./
COPY server/src ./server/src
COPY server/vendor/Fantasy/nupkg ./server/vendor/Fantasy/nupkg
COPY shared ./shared
COPY infrastructure/topology/Fantasy.config ./infrastructure/topology/Fantasy.config
RUN dotnet publish server/src/Hosts/AiNative.${HOST}/AiNative.${HOST}.csproj -c Release -o /app --nologo && printf 'AiNative.%s.dll' "$HOST" > /app/entrypoint-name
FROM mcr.microsoft.com/dotnet/aspnet:10.0.4
WORKDIR /app
COPY --from=build /app .
COPY --chown=1654:1654 --from=build /app/Fantasy.config /app/Fantasy.config
COPY infrastructure/topology/entrypoint.sh /app/entrypoint.sh
RUN mkdir -p /var/lib/ainative/outbox /var/lib/ainative/replay && chown -R 1654:1654 /var/lib/ainative
USER 1654
ENTRYPOINT ["sh", "/app/entrypoint.sh"]
