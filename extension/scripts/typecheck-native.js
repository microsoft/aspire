'use strict';

// Type-checks the extension with the TypeScript 7 native (Go) compiler.
//
// TypeScript 7 ships no JavaScript compiler API, so `typescript` stays aliased to the
// `@typescript/typescript6` compatibility package for everything that imports the module, and the
// native compiler is installed under the `@typescript/native` alias. See "Running Side-by-Side with
// TypeScript 6.0" in https://devblogs.microsoft.com/typescript/announcing-typescript-7-0/.
//
// The compiler is resolved through Node's module resolution and the package's own `bin` field
// rather than a path such as `node_modules/@typescript/native/bin/tsc`. Two things make the literal
// path wrong to depend on: which directory a package manager installs an aliased package into is
// not contractual (npm, Yarn, pnpm and Bun all differ, and pnpm's default layout is not flat), and
// the `bin` entry is the package's own declaration of where its executable lives. Resolving
// `node_modules/.bin/tsc` instead is also wrong, because `@typescript/old` — pulled in transitively
// by the TypeScript 6 compatibility package — declares a `tsc` bin too, and which one wins the
// shim is decided by install order.

const { spawnSync } = require('child_process');
const fs = require('fs');
const path = require('path');

const nativePackageName = '@typescript/native';
const extensionRoot = path.resolve(__dirname, '..');

// Every project the extension compiles needs to pass the native compiler, not only the one webpack
// and the unit tests build. tsconfig.e2e.json covers src/test-e2e, which is compiled separately by
// `compile-e2e` and is otherwise excluded from tsconfig.json.
const projects = ['tsconfig.json', 'tsconfig.e2e.json'];

function resolveNativeCompiler() {
    let packageJsonPath;
    try {
        packageJsonPath = require.resolve(`${nativePackageName}/package.json`, { paths: [extensionRoot] });
    }
    catch {
        throw new Error(`Could not resolve "${nativePackageName}". Run "corepack yarn install" in ${extensionRoot} first.`);
    }

    const packageJson = JSON.parse(fs.readFileSync(packageJsonPath, 'utf8'));

    // The `bin` field is either a string (single executable named after the package) or a map of
    // executable name to path. typescript@7 publishes { "tsc": "./bin/tsc" }.
    const bin = packageJson.bin;
    const relativeBinPath = typeof bin === 'string' ? bin : bin?.tsc;
    if (!relativeBinPath) {
        throw new Error(`"${nativePackageName}" (${packageJson.name}@${packageJson.version}) declares no "tsc" bin: ${JSON.stringify(bin)}`);
    }

    const binPath = path.resolve(path.dirname(packageJsonPath), relativeBinPath);
    if (!fs.existsSync(binPath)) {
        throw new Error(`"${nativePackageName}" declares its tsc bin at ${relativeBinPath}, but ${binPath} does not exist.`);
    }

    return { binPath, version: packageJson.version };
}

function main() {
    const { binPath, version } = resolveNativeCompiler();
    console.log(`Type-checking with the TypeScript ${version} native compiler (${path.relative(extensionRoot, binPath)}).`);

    for (const project of projects) {
        const projectPath = path.join(extensionRoot, project);
        if (!fs.existsSync(projectPath)) {
            throw new Error(`Expected to type-check ${project}, but it does not exist in ${extensionRoot}.`);
        }

        console.log(`  → ${project}`);

        // The bin is a Node script without a portable shebang on Windows, so it is run through the
        // current Node executable rather than executed directly.
        const result = spawnSync(process.execPath, [binPath, '--noEmit', '-p', projectPath], {
            cwd: extensionRoot,
            stdio: 'inherit',
        });

        if (result.error) {
            throw result.error;
        }

        if (result.status !== 0) {
            process.exit(result.status ?? 1);
        }
    }
}

try {
    main();
}
catch (error) {
    console.error(error instanceof Error ? error.message : error);
    process.exit(1);
}
