FROM mcr.microsoft.com/dotnet/sdk:11.0.100-rc.1 AS build
WORKDIR /src
COPY Gateway.cs .
RUN dotnet publish Gateway.cs -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:11.0.0-rc.1
WORKDIR /app
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet","Gateway.dll"]
