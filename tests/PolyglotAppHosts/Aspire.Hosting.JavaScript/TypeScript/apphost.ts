import { createBuilder } from './.modules/aspire.js';

const builder = await createBuilder();

const nodeApp = await builder.addNodeApp('node-app', './node-app', 'server.js');
await nodeApp.withNpm({ install: false, installCommand: 'install', installArgs: ['--ignore-scripts'] });
await nodeApp.withBun({ install: false, installArgs: ['--frozen-lockfile'] });
await nodeApp.withYarn({ install: false, installArgs: ['--immutable'] });
await nodeApp.withPnpm({ install: false, installArgs: ['--frozen-lockfile'] });
await nodeApp.withBuildScript('build', { args: ['--mode', 'production'] });
await nodeApp.withRunScript('dev', { args: ['--host', '0.0.0.0'] });
const _nodeAppName = await nodeApp.name();
const _nodeAppCommand = await nodeApp.command();
const _nodeAppWorkingDirectory = await nodeApp.workingDirectory();

const javaScriptApp = await builder.addJavaScriptApp('javascript-app', './javascript-app', { runScriptName: 'start' });
await javaScriptApp.withEnvironment('NODE_ENV', 'development');
const _javaScriptAppName = await javaScriptApp.name();
const _javaScriptAppCommand = await javaScriptApp.command();
const _javaScriptAppWorkingDirectory = await javaScriptApp.workingDirectory();

const viteApp = await builder.addViteApp('vite-app', './vite-app', { runScriptName: 'dev' });
await viteApp.withViteConfig('./vite.custom.config.ts');
await viteApp.withPnpm({ install: false, installArgs: ['--prod'] });
await viteApp.withBuildScript('build', { args: ['--mode', 'production'] });
await viteApp.withRunScript('dev', { args: ['--host'] });
const _viteAppName = await viteApp.name();
const _viteAppCommand = await viteApp.command();
const _viteAppWorkingDirectory = await viteApp.workingDirectory();

const nextJsApp = await builder.addNextJsApp('nextjs-app', './nextjs-app', { runScriptName: 'dev' });
await nextJsApp.disableBuildValidation();
await nextJsApp.withNpm({ install: false, installCommand: 'ci' });
const _nextJsAppName = await nextJsApp.name();
const _nextJsAppCommand = await nextJsApp.command();
const _nextJsAppWorkingDirectory = await nextJsApp.workingDirectory();

const staticSiteApp = await builder.addJavaScriptApp('static-site-app', './static-site-app');
await staticSiteApp.publishAsStaticWebsite({
    apiPath: '/api',
    apiTarget: nodeApp,
    outputPath: 'dist',
    stripPrefix: true,
    targetEndpointName: 'http',
});

await builder.addJavaScriptApp('node-server-app', './node-server-app')
    .publishAsNodeServer('server.js', { outputPath: 'build' });

await builder.addJavaScriptApp('npm-script-app', './npm-script-app')
    .publishAsNpmScript({ startScriptName: 'start', runScriptArguments: '-- --port $PORT' });

await builder.build().run();
