import { createBuilder } from './.modules/aspire.js';

const builder = await createBuilder();

const nodeApp = await builder.addNodeApp('node-app', './node-app', 'server.js');
await nodeApp.withNpm({ install: false, installCommand: 'install', installArgs: ['--ignore-scripts'] });
await nodeApp.withBun({ install: false, installArgs: ['--frozen-lockfile'] });
await nodeApp.withYarn({ install: false, installArgs: ['--immutable'] });
await nodeApp.withPnpm({ install: false, installArgs: ['--frozen-lockfile'] });
await nodeApp.withBuildScript('build', { args: ['--mode', 'production'] });
await nodeApp.withRunScript('dev', { args: ['--host', '0.0.0.0'] });

const javaScriptApp = await builder.addJavaScriptApp('javascript-app', './javascript-app', { runScriptName: 'start' });
await javaScriptApp.withEnvironment('NODE_ENV', 'development');

const viteApp = await builder.addViteApp('vite-app', './vite-app', { runScriptName: 'dev' });
await viteApp.withViteConfig('./vite.custom.config.ts');
await viteApp.withPnpm({ install: false, installArgs: ['--prod'] });
await viteApp.withBuildScript('build', { args: ['--mode', 'production'] });
await viteApp.withRunScript('dev', { args: ['--host'] });

await builder.build().run();
