# Juice Observability Platform API (and its workers). Build from the repository root:
#   docker build -t juice-observability-api .
FROM mcr.microsoft.com/dotnet/sdk:10.0.402 AS build
WORKDIR /src
COPY Directory.Build.props Directory.Packages.props ./
COPY src/ src/
RUN dotnet publish src/Juice.Observability.Api -c Release -o /app --no-self-contained

FROM mcr.microsoft.com/dotnet/aspnet:10.0.9-alpine3.24
# Alpine has no ICU; the platform needs no culture data.
ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=true \
    ASPNETCORE_URLS=http://+:8080
# krb5-libs: Npgsql probes for GSSAPI at start and logs an error without it.
RUN apk add --no-cache krb5-libs
WORKDIR /app
COPY --from=build /app .
USER $APP_UID
EXPOSE 8080
HEALTHCHECK --interval=10s --timeout=3s --retries=12 CMD wget -q -O - http://127.0.0.1:8080/health/ready || exit 1
ENTRYPOINT ["dotnet", "Juice.Observability.Api.dll"]
