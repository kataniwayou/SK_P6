# Multi-stage build for BaseApi.Service (INFRA-05).
# Offline build: no `# syntax=` frontend (would pull docker/dockerfile from the registry at build);
# nothing here needs BuildKit RUN --mount, so the default frontend is used.
# Stage 1 (build): SDK image restores + publishes a Release build.
# Stage 2 (runtime): aspnet image runs the published output as non-root.

FROM mcr.microsoft.com/dotnet/sdk:8.0-bookworm-slim AS build
WORKDIR /src

# Copy csproj files first for layer-cached restore (D-05 build-context discipline).
# BaseApi.Service depends on BaseApi.Core, which depends on Messaging.Contracts
# (Phase 17 shared-L2-root extract); copy all three manifests so restore resolves
# the full project graph without the rest of the source.
# Offline restore: NuGet.config (nuget.org cleared, offline feed) + the nugets/ local feed.
COPY ["NuGet.config", "Directory.Packages.props", "Directory.Build.props", "global.json", "./"]
COPY nugets/ nugets/
COPY ["src/Messaging.Contracts/Messaging.Contracts.csproj", "src/Messaging.Contracts/"]
COPY ["src/BaseApi.Core/BaseApi.Core.csproj", "src/BaseApi.Core/"]
COPY ["src/BaseApi.Service/BaseApi.Service.csproj", "src/BaseApi.Service/"]
RUN dotnet restore "src/BaseApi.Service/BaseApi.Service.csproj"

# Copy the rest of the source and publish.
COPY src/ src/
RUN dotnet publish "src/BaseApi.Service/BaseApi.Service.csproj" -c Release -o /publish --no-restore /p:UseAppHost=false

# ---- Runtime stage ----
FROM mcr.microsoft.com/dotnet/aspnet:8.0-bookworm-slim AS runtime
WORKDIR /app
# Offline build: NO `apt-get install wget` (needs the Debian apt network). k8s uses httpGet probes
# (kubelet-side, no in-container tool) — no wget/curl needed in the image.
COPY --from=build /publish .
USER app
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "BaseApi.Service.dll"]
