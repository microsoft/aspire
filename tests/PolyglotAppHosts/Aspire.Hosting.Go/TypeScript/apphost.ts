import { createBuilder } from './.modules/aspire.js';

const builder = await createBuilder();

// Basic Go app — go run .
const api = await builder.addGoApp('api', '../go-api');

// Go app with build tags and linker flags
const worker = await builder.addGoApp('worker', '../go-worker')
    .withBuildTags(['netgo', 'osusergo'])
    .withLdFlags('-s -w -X main.version=1.0.0');

// Go app with pre-start lifecycle helpers and debug-friendly compiler flags
const managed = await builder.addGoApp('managed', '../go-managed')
    .withTidy()
    .withVendor()
    .withVet()
    .withRaceDetector()
    .withGcFlags('all=-N -l')
    .withAppArgs(['--config', 'prod.yaml']);

// Go app with headless Delve server for remote debugging (GoLand / VS Code attach)
const debugService = await builder.addGoApp('debug-service', '../go-debug-service')
    .withDelveServer({ port: 2345 });

await builder.build().run();
