# Build from the repository root: docker build -f src/DemoHotel/Dockerfile .
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY src/DemoHotel/ src/DemoHotel/
RUN dotnet publish src/DemoHotel/DemoHotel.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:9.0
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://0.0.0.0:5291
EXPOSE 5291
USER app
ENTRYPOINT ["dotnet", "DemoHotel.dll"]
