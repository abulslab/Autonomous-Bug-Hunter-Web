# Build from the repository root: docker build -f src/DemoShop/Dockerfile .
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY src/DemoShop/ src/DemoShop/
RUN dotnet publish src/DemoShop/DemoShop.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:9.0
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://0.0.0.0:5290
EXPOSE 5290
USER app
ENTRYPOINT ["dotnet", "DemoShop.dll"]
