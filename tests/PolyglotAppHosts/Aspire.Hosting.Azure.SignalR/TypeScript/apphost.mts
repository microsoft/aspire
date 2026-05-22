import { createBuilder } from './.modules/aspire.mjs';

const builder = await createBuilder();
const signalr = await builder.addAzureSignalR('signalr');
await signalr.runAsEmulator();
await builder.build().run();
