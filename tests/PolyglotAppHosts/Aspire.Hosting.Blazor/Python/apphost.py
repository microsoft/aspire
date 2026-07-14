from aspire_app import OtlpProtocol, create_builder


with create_builder() as builder:
    weather_api = builder.add_project("weatherapi", "./src/WeatherApi", "http")
    time_api = builder.add_project("timeapi", "./src/TimeApi", "https")
    time_api.with_https_endpoint(name="api")

    blazor_app = builder.add_blazor_wasm_project("app", "./src/Client/Client.csproj")
    blazor_app.with_reference(weather_api.get_endpoint("http"))
    blazor_app.with_reference(time_api.get_endpoint("api"))

    gateway = builder.add_blazor_gateway("gateway")
    gateway.with_external_http_endpoints()
    gateway.with_otlp_exporter(protocol=OtlpProtocol.HTTP_PROTOBUF)
    gateway.with_blazor_client_app(blazor_app)

    builder.run()
