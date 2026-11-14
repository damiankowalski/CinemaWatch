# CinemaWatch SaaS web app with the poller (docs/SAAS.md §4.4). Build from the repository root:
#   docker build -f src/CinemaWatch.Web/Dockerfile -t cinemawatch-web .
# CI builds it on every push (container test) and publishes it to GHCR for tags web-v* (.github/workflows/web-image.yml).

# Build: the SDK on Ubuntu 26.04 (the server's release).
FROM mcr.microsoft.com/dotnet/sdk:10.0-resolute AS build
WORKDIR /src
# Restore first: this layer is reused while only the code changes.
COPY global.json Directory.Build.props ./
COPY src/CinemaWatch.Core/CinemaWatch.Core.csproj src/CinemaWatch.Core/
COPY src/CinemaWatch.Server/CinemaWatch.Server.csproj src/CinemaWatch.Server/
COPY src/CinemaWatch.Web/CinemaWatch.Web.csproj src/CinemaWatch.Web/
RUN dotnet restore src/CinemaWatch.Web/CinemaWatch.Web.csproj
COPY src/CinemaWatch.Core/ src/CinemaWatch.Core/
COPY src/CinemaWatch.Server/ src/CinemaWatch.Server/
COPY src/CinemaWatch.Web/ src/CinemaWatch.Web/
RUN dotnet publish src/CinemaWatch.Web/CinemaWatch.Web.csproj -c Release --no-restore -o /app -p:UseAppHost=false

# Run: "chiseled" = only the files .NET needs, no shell, no package manager, the non-root user "app" (UID 1654),
# port 8080; "extra" = with ICU (Polish dates) and tzdata (Europe/Warsaw), which the app checks at start-up.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-resolute-chiseled-extra
ARG VERSION=dev
LABEL org.opencontainers.image.source="https://github.com/damiankowalski/CinemaWatch" \
      org.opencontainers.image.title="cinemawatch-web" \
      org.opencontainers.image.description="CinemaWatch SaaS: web app and poller (MVP 0)" \
      org.opencontainers.image.licenses="PolyForm-Noncommercial-1.0.0" \
      org.opencontainers.image.version="${VERSION}"
WORKDIR /app
COPY --from=build /app ./
ENTRYPOINT ["dotnet", "CinemaWatch.Web.dll"]
