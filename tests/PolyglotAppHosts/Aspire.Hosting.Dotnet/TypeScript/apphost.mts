import { createBuilder } from './.aspire/modules/aspire.mjs';

const builder = await createBuilder();

const project = await builder.addDotnetProject('project', './src/Project/Project.csproj');
await project.withReplicas(2);
await project.disableForwardedHeaders();
await project.withEndpointsInEnvironment(['http']);
const _projectName = await project.name();
const _projectCommand = await project.command();
const _projectWorkingDirectory = await project.workingDirectory();

await builder.build().run();
