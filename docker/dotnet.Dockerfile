# One Dockerfile for all four .NET services; compose picks the project with the PROJECT build arg.
#   docker build -f docker/dotnet.Dockerfile --build-arg PROJECT=SyncHub.SupportApi .
# The worker has no web server, so compose gives it the smaller runtime image via RUNTIME_IMAGE.
ARG RUNTIME_IMAGE=mcr.microsoft.com/dotnet/aspnet:10.0

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG PROJECT
WORKDIR /src
# Restore from the project files first, so the (slow) restore layer is cached until a
# .csproj changes, not on every code edit.
COPY src/Directory.Build.props ./
COPY src/SyncHub.Core/SyncHub.Core.csproj SyncHub.Core/
COPY src/SyncHub.Observability/SyncHub.Observability.csproj SyncHub.Observability/
COPY src/${PROJECT}/${PROJECT}.csproj ${PROJECT}/
RUN dotnet restore ${PROJECT}/${PROJECT}.csproj
COPY src/ .
RUN dotnet publish ${PROJECT}/${PROJECT}.csproj -c Release -o /app --no-restore -p:UseAppHost=false

FROM ${RUNTIME_IMAGE}
ARG PROJECT
WORKDIR /app
COPY --from=build /app .
# /keys holds the directory's token signing key on a named volume. The .NET images run as the
# non-root "app" user, so the folder must belong to it before Docker copies it into the volume.
USER root
RUN mkdir -p /keys && chown app:app /keys
USER app
ENV APP_DLL=${PROJECT}.dll \
    ASPNETCORE_ENVIRONMENT=Docker \
    DOTNET_ENVIRONMENT=Docker \
    Urls=http://+:8080
EXPOSE 8080
ENTRYPOINT ["sh", "-c", "exec dotnet \"$APP_DLL\""]
