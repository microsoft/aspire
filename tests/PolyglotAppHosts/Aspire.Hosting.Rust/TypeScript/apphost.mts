import { createBuilder } from './.aspire/modules/aspire.mjs';

const builder = await createBuilder();

// Basic Rust app - cargo run
await builder.addRustApp('api', '../rust-api');

// Workspace member built in release mode with an explicit binary target and features
const worker = await builder.addRustApp('worker', '../rust-workspace');
await worker.withCargoPackage('worker');
await worker.withCargoBinTarget('worker-cli');
await worker.withCargoFeatures(['telemetry', 'postgres']);
await worker.withCargoReleaseBuild({ releaseBuild: true });
await worker.withCargoLocked({ locked: true });

// Custom cargo profile and target triple, driven from a manifest outside the app directory
const tool = await builder.addRustApp('tool', '../rust-tools');
await tool.withCargoManifestPath('../rust-tools/Cargo.toml');
await tool.withCargoProfile('dist');
await tool.withCargoTarget('x86_64-unknown-linux-musl');
await tool.withCargoArgs(['--timings']);

// Cargo example target
const sample = await builder.addRustApp('sample', '../rust-samples');
await sample.withCargoExample('hello');

await builder.build().run();
