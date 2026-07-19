# Offline build: no `# syntax=` frontend (would pull docker/dockerfile from the registry); no RUN --mount used.
# Phase 28 (SAMPLE-02) — first runnable Processor.Sample container.
# Mirrors src/Orchestrator/Dockerfile's multi-stage net8.0 structure, swapping the COPY restore-cache
# list + publish target to Processor.Sample's reference closure (Messaging.Contracts + BaseConsole.Core
# + BaseProcessor.Core + Processor.Sample). Runtime base is aspnet:8.0 (NOT runtime:8.0) because
# BaseConsole.Core carries FrameworkReference Microsoft.AspNetCore.App for the embedded Kestrel
# health listener, which needs the ASP.NET Core shared framework at runtime. Port 8082 matches
# ConsoleHealth:Port (appsettings.json).
#
# The SourceHash.targets compute/emit runs INSIDE this Linux `dotnet publish` — this is where the
# Pitfall-1 cross-OS reproducibility constraint (host SDK build == Linux Docker build) is exercised.
# scripts/verify-sourcehash-reproducible.ps1 asserts the host and Docker embedded hashes are equal.
FROM mcr.microsoft.com/dotnet/sdk:8.0-bookworm-slim AS build
WORKDIR /src
# Cache layer: restore depends only on csproj + props + lock files.
# Offline restore: NuGet.config (nuget.org cleared, offline feed) + the nugets/ local feed.
COPY ["NuGet.config", "Directory.Packages.props", "Directory.Build.props", "global.json", "./"]
COPY nugets/ nugets/
COPY ["src/Messaging.Contracts/Messaging.Contracts.csproj", "src/Messaging.Contracts/"]
COPY ["src/BaseConsole.Core/BaseConsole.Core.csproj", "src/BaseConsole.Core/"]
COPY ["src/BaseProcessor.Core/BaseProcessor.Core.csproj", "src/BaseProcessor.Core/"]
COPY ["src/Processor.Sample/Processor.Sample.csproj", "src/Processor.Sample/"]
RUN dotnet restore "src/Processor.Sample/Processor.Sample.csproj"
# Build layer: full source — the SourceHash target folds the impl .cs here, embedding the 64-hex.
COPY src/ src/
RUN dotnet publish "src/Processor.Sample/Processor.Sample.csproj" -c Release -o /publish --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0-bookworm-slim AS runtime
WORKDIR /app
# Offline build: NO `apt-get install wget`. k8s uses httpGet probes; compose uses the app's dotnet
# self-probe (`dotnet Processor.Sample.dll --healthcheck`) — no wget/curl needed.
COPY --from=build /publish .
USER app
ENV ASPNETCORE_URLS=http://+:8082
EXPOSE 8082
ENTRYPOINT ["dotnet", "Processor.Sample.dll"]
