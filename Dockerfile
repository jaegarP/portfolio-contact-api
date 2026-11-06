# ---------------------------------------------------------------
# Contact API — container image
# Build:  docker build -t contact-api .
# Run with local storage: docker compose up --build   (see docker-compose.yml)
# ---------------------------------------------------------------

# 1) Build & publish with the .NET 10 SDK
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/ContactApi/ContactApi.csproj src/ContactApi/
RUN dotnet restore src/ContactApi/ContactApi.csproj
COPY src/ContactApi/ src/ContactApi/
RUN dotnet publish src/ContactApi/ContactApi.csproj -c Release -o /home/site/wwwroot --no-restore

# 2) Run on the official Azure Functions host image (.NET 10 isolated worker)
FROM mcr.microsoft.com/azure-functions/dotnet-isolated:4-dotnet-isolated10.0
ENV AzureWebJobsScriptRoot=/home/site/wwwroot \
    AzureFunctionsJobHost__Logging__Console__IsEnabled=true
COPY --from=build /home/site/wwwroot /home/site/wwwroot
EXPOSE 80
