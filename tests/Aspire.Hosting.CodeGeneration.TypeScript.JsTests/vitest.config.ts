import { defineConfig } from 'vitest/config';
import path from 'path';

const resources = path.resolve(__dirname, '../../src/Aspire.Hosting.CodeGeneration.TypeScript/Resources');

export default defineConfig({
    resolve: {
        alias: {
            '@aspire/transport': path.resolve(resources, 'transport.mts'),
            '@aspire/base': path.resolve(resources, 'base.mts'),
            // Integration playgrounds normally import a restored SDK. Exercise their
            // host protocol without running CLI codegen; tests register the handle types they use.
            '../.aspire/modules/base.mjs': path.resolve(resources, 'base.mts'),
            '../.aspire/modules/transport.mjs': path.resolve(resources, 'transport.mts'),
            '../.aspire/modules/aspire.mjs': path.resolve(resources, 'base.mts'),
            // The source files import 'vscode-jsonrpc/node.js' which needs to
            // resolve from this test project's node_modules, not the source tree.
            'vscode-jsonrpc': path.resolve(__dirname, 'node_modules/vscode-jsonrpc'),
        },
    },
    test: {
        include: ['tests/**/*.test.ts'],
        globals: false,
        environment: 'node',
        testTimeout: 10_000,
    },
});
