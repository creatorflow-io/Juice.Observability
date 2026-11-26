# syntax=docker/dockerfile:1.10
# Juice Observability: Platform API (and its workers) serving Juice Console. Build from the repository root, with the GitHub Packages
# credentials nuget.config names (the Juice.Notifications alphas are published there) as build secrets, never in a layer:
#   docker build --secret id=github_package_username,env=GITHUB_PACKAGE_USERNAME --secret id=github_package_token,env=GITHUB_PACKAGE_TOKEN \
#     -t juice-observability-api .
FROM node:24.22.0-alpine AS console
WORKDIR /console
COPY console/package.json console/package-lock.json ./
RUN npm ci --no-audit --no-fund
COPY console/ ./
RUN npx ng build juice-console --configuration production

FROM mcr.microsoft.com/dotnet/sdk:10.0.402 AS build
WORKDIR /src
COPY Directory.Build.props Directory.Packages.props nuget.config ./
COPY src/ src/
RUN --mount=type=secret,id=github_package_username,env=GITHUB_PACKAGE_USERNAME \
    --mount=type=secret,id=github_package_token,env=GITHUB_PACKAGE_TOKEN \
    dotnet publish src/Juice.Observability.Host -c Release -o /app --no-self-contained

FROM mcr.microsoft.com/dotnet/aspnet:10.0.9-alpine3.24
# Alpine has no ICU; the platform needs no culture data.
ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=true \
    ASPNETCORE_URLS=http://+:8080
# krb5-libs: Npgsql probes for GSSAPI at start and logs an error without it.
RUN apk add --no-cache krb5-libs
WORKDIR /app
COPY --from=build /app .
# Juice Console, served by the API on the same origin. A deployment mounts its own wwwroot/config.json.
COPY --from=console /console/dist/juice-console/browser wwwroot/
USER $APP_UID
EXPOSE 8080
HEALTHCHECK --interval=10s --timeout=3s --retries=12 CMD wget -q -O - http://127.0.0.1:8080/health/ready || exit 1
ENTRYPOINT ["dotnet", "Juice.Observability.Host.dll"]
