import { EntrypointType, createBuilder } from './.modules/aspire.js';

const builder = await createBuilder();
await builder.addPythonApp('python-script', '.', 'main.py');
await builder.addPythonModule('python-module', '.', 'uvicorn');
await builder.addPythonExecutable('python-executable', '.', 'pytest');

const uvicorn = await builder.addUvicornApp('python-uvicorn', '.', 'main:app');

await uvicorn.withVirtualEnvironment('.venv', { createIfNotExists: false });
await uvicorn.withDebugging();
await uvicorn.withEntrypoint(EntrypointType.Module, 'uvicorn');
await uvicorn.withPip({ install: true, installArgs: ['install', '-r', 'requirements.txt'] });
await uvicorn.withUv({ install: false, args: ['sync'] });

await builder.build().run();
