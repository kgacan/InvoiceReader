# Build
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY nuget.config ./
COPY src/InvoiceReader.Web/InvoiceReader.Web.csproj src/InvoiceReader.Web/
RUN dotnet restore src/InvoiceReader.Web/InvoiceReader.Web.csproj
COPY src/ src/
RUN dotnet publish src/InvoiceReader.Web/InvoiceReader.Web.csproj -c Release -o /app --no-restore

# Litestream: continuous SQLite backup (deploy/litestream.yml). Checksum-pinned release, linux-amd64.
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS litestream
ARG LITESTREAM_VERSION=0.3.13
ARG LITESTREAM_SHA256=eb75a3de5cab03875cdae9f5f539e6aedadd66607003d9b1e7a9077948818ba0
RUN curl -fsSL -o /tmp/litestream.tar.gz \
      "https://github.com/benbjohnson/litestream/releases/download/v${LITESTREAM_VERSION}/litestream-v${LITESTREAM_VERSION}-linux-amd64.tar.gz" \
 && echo "${LITESTREAM_SHA256}  /tmp/litestream.tar.gz" | sha256sum -c - \
 && tar -xzf /tmp/litestream.tar.gz -C /usr/local/bin litestream

# Run
FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app .
COPY --from=litestream /usr/local/bin/litestream /usr/local/bin/litestream
COPY deploy/litestream.yml /etc/litestream.yml
COPY deploy/docker-entrypoint.sh /app/docker-entrypoint.sh
RUN chmod +x /app/docker-entrypoint.sh
ENV ASPNETCORE_URLS=http://+:8080 \
    DataDir=/app/data
EXPOSE 8080
ENTRYPOINT ["/app/docker-entrypoint.sh"]
