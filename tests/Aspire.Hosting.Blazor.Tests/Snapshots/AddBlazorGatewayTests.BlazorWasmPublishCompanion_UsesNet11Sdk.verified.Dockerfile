FROM mcr.microsoft.com/dotnet/sdk:11.0.100-rc.1 AS build
RUN curl --fail --show-error --location https://dot.net/v1/dotnet-install.sh -o {TempPath}dotnet-install.sh && \
    bash {TempPath}dotnet-install.sh --version 11.0.100-rc.1.26425.128 --install-dir /opt/client-dotnet --no-path && \
    rm {TempPath}dotnet-install.sh
WORKDIR /src
COPY . .
WORKDIR /src/Blazor
RUN /opt/client-dotnet/dotnet publish "Blazor.csproj" -f net11.0 -c Release -o /app/publish

# Prefix asset paths and add SPA fallback endpoint
WORKDIR {TempPath}
RUN mkdir -p /app/output/wwwroot/app && \
    cp -r /app/publish/wwwroot/* /app/output/wwwroot/app/ && \
    cp "/src/.aspire/scripts/PrefixEndpoints.cs" {TempPath}PrefixEndpoints.cs && \
    dotnet run {TempPath}PrefixEndpoints.cs -- \
        /app/publish/*.staticwebassets.endpoints.json \
        app \
        /app/output/app.endpoints.json