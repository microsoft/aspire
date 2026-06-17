import aspire.*;

void main() throws Exception {
        var builder = DistributedApplication.CreateBuilder();

        var weatherApi = builder.addProject("weatherapi", "./src/WeatherApi", "http");
        var timeApi = builder.addProject("timeapi", "./src/TimeApi", "https");
        timeApi.withHttpsEndpoint(new WithHttpsEndpointOptions().name("api"));

        var blazorApp = builder.addBlazorWasmProject("app", "./src/Client/Client.csproj");
        blazorApp.withReference(weatherApi.getEndpoint("http"));
        blazorApp.withReference(timeApi.getEndpoint("api"));

        var gateway = builder.addBlazorGateway("gateway")
            .withExternalHttpEndpoints()
            .withOtlpExporter(OtlpProtocol.HTTP_PROTOBUF);

        gateway.withBlazorClientApp(blazorApp);

        builder.build().run();
    }
