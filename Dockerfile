FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy csproj and restore
COPY src/TalkingPointsSummary.Core/TalkingPointsSummary.Core.csproj src/TalkingPointsSummary.Core/
COPY src/TalkingPointsSummary/TalkingPointsSummary.csproj src/TalkingPointsSummary/
RUN dotnet restore src/TalkingPointsSummary/TalkingPointsSummary.csproj

# Copy everything and build
COPY . .
WORKDIR /src/src/TalkingPointsSummary
RUN dotnet publish -c Release -o /app/publish --no-restore

# Runtime image
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .

# Default: run as worker service (no args)
# For CLI: docker exec <container> dotnet TalkingPointsSummary.dll add-parent ...
ENTRYPOINT ["dotnet", "TalkingPointsSummary.dll"]
