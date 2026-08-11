// Regression coverage for the TypeScript AppHost scaffold's eslint.config.mjs.
//
// The scaffolded ESLint config exists for one purpose: catch unawaited AppHost
// promises (such as a forgotten `await builder.build().run();`) at lint time so
// they never reach `aspire run`. Asserting the config *file content* — as the
// C# unit tests already do — only protects against the static shape. This suite
// runs ESLint against the actual scaffolded resource files to prove the rule is
// wired up correctly and fires for thenable AppHost call chains.
//
// Strategy:
// 1. Copy the scaffolded `eslint.config.mjs` + `tsconfig.apphost.json` from
//    the ts-starter template (the single physical source the codegen
//    project also embeds) into a per-test fixture directory located *inside*
//    this test project so node_modules resolution can find `eslint` and
//    `typescript-eslint`.
// 2. Drop a fixture `apphost.mts` containing the scenario under test.
// 3. Invoke `ESLint` programmatically with `cwd` set to the fixture dir so the
//    flat config is loaded exactly as the scaffolded project would load it.
// 4. Inspect the lint result for `@typescript-eslint/no-floating-promises`
//    reports.

import { describe, it, expect, beforeAll, beforeEach, afterEach, afterAll } from 'vitest';
import { ESLint } from 'eslint';
import {
    copyFileSync,
    existsSync,
    mkdirSync,
    mkdtempSync,
    rmSync,
    writeFileSync,
} from 'node:fs';
import { join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const projectDir = resolve(fileURLToPath(import.meta.url), '..', '..');
const scaffoldSourceDir = resolve(
    projectDir,
    '..',
    '..',
    'src',
    'Aspire.Cli',
    'Templating',
    'Templates',
    'ts-starter'
);

const eslintConfigPath = join(scaffoldSourceDir, 'eslint.config.mjs');
const appHostTsConfigPath = join(scaffoldSourceDir, 'tsconfig.apphost.json');

// Per-test fixtures live under .fixtures/ inside the JsTests project so the
// node_modules lookup chain finds the eslint + typescript-eslint packages
// installed via this project's package.json.
const fixturesRoot = join(projectDir, '.fixtures');

const NoFloatingPromises = '@typescript-eslint/no-floating-promises';

function ensureFixtureRoot(): void {
    if (!existsSync(fixturesRoot)) {
        mkdirSync(fixturesRoot, { recursive: true });
    }
}

function createFixtureDir(): string {
    ensureFixtureRoot();
    const dir = mkdtempSync(join(fixturesRoot, 'eslint-config-'));
    copyFileSync(eslintConfigPath, join(dir, 'eslint.config.mjs'));
    copyFileSync(appHostTsConfigPath, join(dir, 'tsconfig.apphost.json'));
    return dir;
}

function writeAppHost(fixtureDir: string, source: string): void {
    writeFileSync(join(fixtureDir, 'apphost.mts'), source, 'utf8');
}

async function lintAppHost(fixtureDir: string): Promise<ESLint.LintResult[]> {
    const eslint = new ESLint({ cwd: fixtureDir });
    return eslint.lintFiles(['apphost.mts']);
}

function collectFloatingPromiseMessages(results: ESLint.LintResult[]): ESLint.LintMessage[] {
    return results.flatMap((result) =>
        result.messages.filter((message) => message.ruleId === NoFloatingPromises)
    );
}

describe('scaffolded eslint.config.mjs', () => {
    let fixtureDir: string;

    beforeAll(() => {
        // Confirm the scaffold source files actually exist before we promise
        // regression coverage; this fails loudly if the ts-starter template moves.
        expect(existsSync(eslintConfigPath)).toBe(true);
        expect(existsSync(appHostTsConfigPath)).toBe(true);
    });

    beforeEach(() => {
        fixtureDir = createFixtureDir();
    });

    afterEach(() => {
        rmSync(fixtureDir, { recursive: true, force: true });
    });

    afterAll(() => {
        // Best-effort cleanup of the fixtures root so the working tree stays
        // tidy when individual fixture cleanups race with vitest teardown.
        rmSync(fixturesRoot, { recursive: true, force: true });
    });

    it('flags an unawaited builder.build().run() chain as a floating promise', async () => {
        writeAppHost(
            fixtureDir,
            `// Synthetic AppHost stand-in modelled on the scaffolded apphost.mts.
declare const builder: {
    build(): { run(): Promise<void> };
};

builder.build().run();
`
        );

        const results = await lintAppHost(fixtureDir);
        const messages = collectFloatingPromiseMessages(results);

        expect(messages.length).toBeGreaterThan(0);
        expect(messages[0].severity).toBe(2);
    });

    it('flags an unawaited thenable resource-builder call', async () => {
        writeAppHost(
            fixtureDir,
            `// Resource-builder methods return PromiseLike values, so the rule's
// checkThenables: true setting must catch them too.
declare const builder: {
    addContainer(name: string, image: string): PromiseLike<unknown>;
};

builder.addContainer('cache', 'redis:latest');
`
        );

        const results = await lintAppHost(fixtureDir);
        const messages = collectFloatingPromiseMessages(results);

        expect(messages.length).toBeGreaterThan(0);
    });

    it('lints clean when the AppHost promise chain is properly awaited', async () => {
        writeAppHost(
            fixtureDir,
            `declare const builder: {
    build(): { run(): Promise<void> };
};

await builder.build().run();
`
        );

        const results = await lintAppHost(fixtureDir);
        const messages = collectFloatingPromiseMessages(results);

        expect(messages).toEqual([]);
    });

    it('only lints apphost.mts (files glob is respected)', async () => {
        writeAppHost(
            fixtureDir,
            `declare const builder: {
    build(): { run(): Promise<void> };
};

await builder.build().run();
`
        );

        // Sibling .ts file containing an obvious floating promise. The scaffolded
        // config restricts the rule to apphost.mts so this file must lint clean.
        writeFileSync(
            join(fixtureDir, 'other.ts'),
            `declare const work: () => Promise<void>;
work();
`,
            'utf8'
        );

        const eslint = new ESLint({ cwd: fixtureDir });
        const results = await eslint.lintFiles(['apphost.mts', 'other.ts']);

        const appHostFloats = collectFloatingPromiseMessages(
            results.filter((r) => r.filePath.endsWith('apphost.mts'))
        );
        const otherFloats = collectFloatingPromiseMessages(
            results.filter((r) => r.filePath.endsWith('other.ts'))
        );

        expect(appHostFloats).toEqual([]);
        expect(otherFloats).toEqual([]);
    });

    it('flags a .then() chain that lacks .catch() handling', async () => {
        // .then() returns a new Promise. Without .catch()/.finally() the chain
        // is still floating, even though developers commonly think the handler
        // "consumes" the promise.
        writeAppHost(
            fixtureDir,
            `declare const builder: {
    build(): { run(): Promise<void> };
};

builder.build().run().then(() => {
    console.log('AppHost finished');
});
`
        );

        const results = await lintAppHost(fixtureDir);
        const messages = collectFloatingPromiseMessages(results);

        expect(messages.length).toBeGreaterThan(0);
        expect(messages[0].severity).toBe(2);
    });

    it('lints clean when the AppHost promise is suppressed with the void operator', async () => {
        // The ts-eslint rule defaults to ignoreVoid: true. Pinning this lets
        // the project tighten the rule (ignoreVoid: false) deliberately rather
        // than by accident.
        writeAppHost(
            fixtureDir,
            `declare const builder: {
    build(): { run(): Promise<void> };
};

void builder.build().run();
`
        );

        const results = await lintAppHost(fixtureDir);
        const messages = collectFloatingPromiseMessages(results);

        expect(messages).toEqual([]);
    });

    it('lints clean when an async wrapper returns the AppHost promise to its caller', async () => {
        // Returning the promise delegates handling to the caller, which is a
        // legitimate way to factor a bootstrap function. The rule recognises
        // this and stays quiet.
        writeAppHost(
            fixtureDir,
            `declare const builder: {
    build(): { run(): Promise<void> };
};

async function start(): Promise<void> {
    return builder.build().run();
}

await start();
`
        );

        const results = await lintAppHost(fixtureDir);
        const messages = collectFloatingPromiseMessages(results);

        expect(messages).toEqual([]);
    });

    it('flags a floating promise inside a nested async function body', async () => {
        // The rule must walk into function bodies, not just inspect the file's
        // top-level statements. Catches a regression where a parser tweak
        // accidentally narrows the rule's scope.
        writeAppHost(
            fixtureDir,
            `declare const builder: {
    build(): { run(): Promise<void> };
};

async function start(): Promise<void> {
    builder.build().run();
}

await start();
`
        );

        const results = await lintAppHost(fixtureDir);
        const messages = collectFloatingPromiseMessages(results);

        expect(messages.length).toBeGreaterThan(0);
        expect(messages[0].severity).toBe(2);
    });
});
