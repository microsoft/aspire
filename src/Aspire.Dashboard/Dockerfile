FROM mcr.microsoft.com/dotnet/aspnet:8.0

WORKDIR /app
COPY /publish .

ENTRYPOINT [ "dotnet", "Aspire.Dashboard.dll" ]

ENV \
    # Unset ASPNETCORE_HTTP_PORTS from base image
    ASPNETCORE_HTTP_PORTS= \
    # Aspire Dashboard environment variables
    ASPNETCORE_URLS=http://+:18888 \
    DOTNET_DASHBOARD_OTLP_ENDPOINT_URL=http://+:18889 \
    DOTNET_DASHBOARD_OTLP_HTTP_ENDPOINT_URL=http://+:18890