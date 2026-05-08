import { createBuilder } from './.modules/aspire.js';

const builder = await createBuilder();

// Basic Go app — go run .
await builder.addGoApp('api', '../go-api');

// Go app with build tags and linker flags via options
await builder.addGoApp('worker', '../go-worker', {
    buildTags: ['netgo', 'osusergo'],
    ldFlags: '-s -w -X main.version=1.0.0',
});

// Go app with pre-start lifecycle helpers and all build options
const managed = await builder.addGoApp('managed', '../go-managed', {
    buildTags: ['integration'],
    ldFlags: '-s -w',
    gcFlags: 'all=-N -l',
    raceDetector: true,
});
await managed.withModTidy();
await managed.withModVendor();
await managed.withModDownload();
await managed.withVetTool();
await managed.withAppArgs(['--config', 'prod.yaml']);

// Go app with headless Delve server for remote debugging (GoLand / VS Code attach)
const debugService = await builder.addGoApp('debug-service', '../go-debug-service');
await debugService.withDelveServer({ port: 2345 });

await builder.build().run();
