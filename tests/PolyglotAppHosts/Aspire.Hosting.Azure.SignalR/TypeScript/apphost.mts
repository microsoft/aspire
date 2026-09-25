import { createBuilder } from './.aspire/modules/aspire.mjs';

const builder = await createBuilder();
const signalr = await builder.addAzureSignalR('signalr');
await signalr.configureInfrastructure(async infrastructure => {
    const service = await infrastructure.getSignalRService();
    await service.disableLocalAuth.set(true);
    const _disableLocalAuth = await service.disableLocalAuth.get();
});
await signalr.runAsEmulator();
await builder.build().run();
