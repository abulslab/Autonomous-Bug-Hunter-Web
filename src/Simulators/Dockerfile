# Build from the repository root: docker build -f src/Simulators/Dockerfile .
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY src/Simulators/ src/Simulators/
RUN dotnet publish src/Simulators/Simulators.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:9.0
WORKDIR /app
COPY --from=build /app .
# The simulators are seeded from the knowledge files.
COPY ground-truth/ /ground-truth/
ENV ASPNETCORE_URLS=http://0.0.0.0:5292 Simulators__GroundTruthDir=/ground-truth
EXPOSE 5292
USER app
ENTRYPOINT ["dotnet", "Simulators.dll"]
