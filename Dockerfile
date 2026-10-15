FROM node:24-bookworm-slim AS web
WORKDIR /build/web
COPY web/package*.json ./
RUN npm ci
COPY web/ ./
RUN npm run check && npm run build

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS api
WORKDIR /build
COPY global.json Directory.Build.props ./
COPY src/Twenty.Core/ src/Twenty.Core/
COPY src/Twenty.Api/ src/Twenty.Api/
COPY fixtures/research/ fixtures/research/
COPY --from=web /build/src/Twenty.Api/wwwroot/ src/Twenty.Api/wwwroot/
RUN dotnet restore src/Twenty.Api/Twenty.Api.csproj --locked-mode && dotnet publish src/Twenty.Api/Twenty.Api.csproj -c Release --no-restore -o /out /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0
ARG COMMIT=local
ENV ASPNETCORE_HTTP_PORTS=8080 TWENTY_COMMIT=$COMMIT DOTNET_EnableDiagnostics=0 TWENTY_DATA_ROOT=/data
LABEL org.opencontainers.image.source="https://github.com/Jellman86/twenty" org.opencontainers.image.description="Twenty research workbench"
WORKDIR /app
COPY --from=api /out/ ./
RUN mkdir -p /data && chown "$APP_UID:$APP_UID" /data
USER $APP_UID
EXPOSE 8080
HEALTHCHECK --interval=30s --timeout=10s --start-period=10s CMD ["dotnet", "Twenty.Api.dll", "--health-check"]
ENTRYPOINT ["dotnet", "Twenty.Api.dll"]
