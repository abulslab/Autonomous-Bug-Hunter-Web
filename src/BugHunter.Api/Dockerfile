# Build from the repository root: docker build -f src/BugHunter.Api/Dockerfile .
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY src/BugHunter.Api/BugHunter.Api.csproj src/BugHunter.Api/
RUN dotnet restore src/BugHunter.Api/BugHunter.Api.csproj
COPY src/BugHunter.Api/ src/BugHunter.Api/
RUN dotnet publish src/BugHunter.Api/BugHunter.Api.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:9.0
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://0.0.0.0:5280 \
    Storage__DataDir=/data \
    Hunter__AutoStartWorker=false
VOLUME /data
EXPOSE 5280
USER app
ENTRYPOINT ["dotnet", "BugHunter.Api.dll"]
