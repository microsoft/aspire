import { createBuilder } from './.aspire/modules/aspire.mjs';

const builder = await createBuilder();

const tool = await builder.addDotnetCliTool("tool", "dotnet-ef");
await tool.withToolPackage("dotnet-ef");
await tool.withToolVersion("10.0.*");
await tool.withToolPrerelease();
await tool.withToolSource("https://example.invalid/feed");
await tool.withToolIgnoreExistingFeeds();
await tool.withToolIgnoreFailedSources();

const project = await builder.addDotnetProject('project', './src/Project/Project.csproj', {
    launchProfileName: 'https',
});
await project.withReplicas(2);
await project.disableForwardedHeaders();
await project.withEndpointsInEnvironment(['http']);
const _projectName = await project.name();
const _projectCommand = await project.command();
const _projectWorkingDirectory = await project.workingDirectory();

await builder.build().run();
