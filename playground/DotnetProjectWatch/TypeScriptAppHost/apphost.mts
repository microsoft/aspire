// SPIKE: a TypeScript AppHost whose C# service is hot reloaded by `aspire run --watch`
// (docs/plans/project-v2-csharpprogram-watch.md, Session 7). The AppHost itself is watched by the guest
// runtime; WatchDemo.Api is watched by Aspire.Hosting.Dotnet in the AppHost server.
import { createBuilder } from './.aspire/modules/aspire.mjs';

const builder = await createBuilder();

await builder.addDotnetProject("api", "../services/WatchDemo.Api")
    .withEnvironment("GREETING_SOURCE", "typescript-apphost")
    .withExternalHttpEndpoints();

await builder.build().run();
