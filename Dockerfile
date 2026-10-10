# Build
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY nuget.config ./
COPY src/InvoiceReader.Web/InvoiceReader.Web.csproj src/InvoiceReader.Web/
RUN dotnet restore src/InvoiceReader.Web/InvoiceReader.Web.csproj
COPY src/ src/
RUN dotnet publish src/InvoiceReader.Web/InvoiceReader.Web.csproj -c Release -o /app --no-restore

# Run
FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8080 \
    DataDir=/app/data
EXPOSE 8080
ENTRYPOINT ["dotnet", "InvoiceReader.Web.dll"]
