# Offline build: no `# syntax=` frontend (would pull docker/dockerfile from the registry); no RUN --mount used.
# Phase 34 (KEEP-01/KEEP-03) — first runnable Keeper container.
# Clones the Orchestrator Dockerfile multi-stage net8.0 structure, swapping the COPY restore-cache
# list + publish target to Keeper's leaner reference closure (Messaging.Contracts + BaseConsole.Core
# + Keeper only — no processor-base or web-api projects, per D-05/D-07). Runtime base is aspnet:8.0 (NOT
# runtime:8.0) because BaseConsole.Core carries FrameworkReference Microsoft.AspNetCore.App for the
# embedded Kestrel health listener, which needs the ASP.NET Core shared framework at runtime
# (Pitfall 3). Port 8083 matches ConsoleHealth:Port.
FROM mcr.microsoft.com/dotnet/sdk:8.0-bookworm-slim AS build
WORKDIR /src
# Cache layer: restore depends only on csproj + props + lock files
# Offline restore: NuGet.config (nuget.org cleared, offline feed) + the nugets/ local feed.
COPY ["NuGet.config", "Directory.Packages.props", "Directory.Build.props", "global.json", "./"]
COPY nugets/ nugets/
COPY ["src/Messaging.Contracts/Messaging.Contracts.csproj", "src/Messaging.Contracts/"]
COPY ["src/BaseConsole.Core/BaseConsole.Core.csproj", "src/BaseConsole.Core/"]
COPY ["src/Keeper/Keeper.csproj", "src/Keeper/"]
RUN dotnet restore "src/Keeper/Keeper.csproj"
# Build layer: full source
COPY src/ src/
RUN dotnet publish "src/Keeper/Keeper.csproj" -c Release -o /publish --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0-bookworm-slim AS runtime
WORKDIR /app
COPY --from=build /publish .
# Offline build: NO `apt-get install wget`. k8s uses httpGet probes; compose uses the app's dotnet
# self-probe (`dotnet Keeper.dll --healthcheck`) — no wget/curl needed.
USER app
ENV ASPNETCORE_URLS=http://+:8083
EXPOSE 8083
ENTRYPOINT ["dotnet", "Keeper.dll"]
