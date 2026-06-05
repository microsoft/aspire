FROM mcr.microsoft.com/dotnet/nightly/yarp:2.3-preview AS yarp
WORKDIR /app
COPY . /app/wwwroot