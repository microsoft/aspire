import { createBuilder, OtlpProtocol } from './.aspire/modules/aspire.mjs';

const builder = await createBuilder();

const weatherApi = await builder.addProject('weatherapi', './src/WeatherApi', { launchProfileOrOptions: 'http' });
const timeApi = await builder.addProject('timeapi', './src/TimeApi', { launchProfileOrOptions: 'https' });
await timeApi.withHttpsEndpoint({ name: 'api' });

const blazorApp = await builder.addBlazorWasmProject('app', './src/Client/Client.csproj');
await blazorApp.withReference(await weatherApi.getEndpoint('http'));
await blazorApp.withReference(await timeApi.getEndpoint('api'));

const gateway = await builder.addBlazorGateway('gateway')
    .withExternalHttpEndpoints()
    .withOtlpExporter({ protocol: OtlpProtocol.HttpProtobuf });

await gateway.withBlazorClientApp(blazorApp);

await builder.build().run();
