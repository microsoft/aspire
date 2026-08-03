import { createBuilder } from './.aspire/modules/aspire.mjs';

const builder = await createBuilder();

const project = await builder.addDotnetProject('project', './src/Project/Project.csproj');
const _projectName = await project.name();
const _projectCommand = await project.command();
const _projectWorkingDirectory = await project.workingDirectory();

await builder.build().run();
