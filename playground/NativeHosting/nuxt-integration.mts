import * as rpc from '../NuxtApp/node_modules/vscode-jsonrpc/node.js';

interface ResourceHandle {
    owner: string;
    name: string;
}

interface NuxtArguments {
    directory: string;
    name: string;
}

const connection = rpc.createMessageConnection(
    new rpc.StreamMessageReader(process.stdin),
    new rpc.StreamMessageWriter(process.stdout),
);

// The unshipped Nuxt projection uses only native executable primitives.
// This host has no generated C# JavaScript resource SDK or CLR resource handles.
connection.onRequest('addNuxt', async (args: NuxtArguments) => {
    const resource = await connection.sendRequest<ResourceHandle>('invokeCore', {
        method: 'addExecutable',
        args: {
            name: args.name,
            directory: args.directory,
            executable: process.execPath,
            arguments: ['node_modules/nuxt/bin/nuxt.mjs', 'dev', '--no-fork', '--no-clear'],
            publish: {
                buildExecutable: process.execPath,
                buildArguments: ['node_modules/nuxt/bin/nuxt.mjs', 'build'],
                entryPoint: '.output/server/index.mjs',
                outputDirectory: '.output',
            },
        },
    });
    return resource;
});
connection.onRequest('withReference', (args: { resource: ResourceHandle; reference: ResourceHandle }) =>
    connection.sendRequest('invokeCore', { method: 'withReference', args }));
connection.onClose(() => process.exit(0));
connection.listen();
