import * as assert from 'assert';
import { existsSync, mkdtempSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { summarizeAppHostLanguages, classifyAppHostPath, classifyAppHostDirectory } from '../utils/appHostLanguage';
import type { CandidateAppHostDisplayInfo } from '../utils/appHostDiscovery';

function c(language: string | null): CandidateAppHostDisplayInfo {
    return { path: '/x', language, status: null };
}

suite('appHostLanguage.summarizeAppHostLanguages', () => {
    test('returns none for empty candidate list', () => {
        assert.strictEqual(summarizeAppHostLanguages([]), 'none');
    });

    test('returns csharp when every candidate is C#', () => {
        assert.strictEqual(summarizeAppHostLanguages([c('csharp'), c('C#')]), 'csharp');
    });

    test('returns typescript for typescript variants', () => {
        assert.strictEqual(summarizeAppHostLanguages([c('typescript'), c('typescript/nodejs')]), 'typescript');
        assert.strictEqual(summarizeAppHostLanguages([c('javascript')]), 'typescript');
    });

    test('returns polyglot for a mix of csharp and typescript', () => {
        assert.strictEqual(summarizeAppHostLanguages([c('csharp'), c('typescript')]), 'polyglot');
    });

    test('returns polyglot when an unknown language is mixed with a known one', () => {
        assert.strictEqual(summarizeAppHostLanguages([c('csharp'), c('python')]), 'polyglot');
    });

    test('returns unknown when no candidate has a recognizable language', () => {
        assert.strictEqual(summarizeAppHostLanguages([c(null), c(null)]), 'unknown');
    });

    test('treats only-other as unknown rather than polyglot', () => {
        // A single non-csharp / non-typescript family with no known sibling
        // should collapse to "unknown" to avoid polluting the polyglot bucket
        // with single-language workspaces we just don't classify yet.
        const result = summarizeAppHostLanguages([c('rust'), c('rust')]);
        // Behavior: rust collapses to 'other', sawOther = true; sawAny = true;
        // distinctFamilies = 1 (just other); falls through to final 'unknown'.
        assert.strictEqual(result, 'unknown');
    });
});

suite('appHostLanguage.classifyAppHostPath', () => {
    test('returns unknown for undefined / empty path', () => {
        assert.strictEqual(classifyAppHostPath(undefined), 'unknown');
        assert.strictEqual(classifyAppHostPath(''), 'unknown');
    });

    test('classifies .csproj and .cs as csharp', () => {
        assert.strictEqual(classifyAppHostPath('/abs/path/AppHost.csproj'), 'csharp');
        assert.strictEqual(classifyAppHostPath('AppHost.cs'), 'csharp');
        assert.strictEqual(classifyAppHostPath('C:\\repos\\My.AppHost.csproj'), 'csharp');
    });

    test('classifies typescript / javascript module variants', () => {
        assert.strictEqual(classifyAppHostPath('apphost.ts'), 'typescript');
        assert.strictEqual(classifyAppHostPath('apphost.mts'), 'typescript');
        assert.strictEqual(classifyAppHostPath('apphost.cts'), 'typescript');
        assert.strictEqual(classifyAppHostPath('apphost.js'), 'typescript');
        assert.strictEqual(classifyAppHostPath('apphost.mjs'), 'typescript');
        assert.strictEqual(classifyAppHostPath('apphost.cjs'), 'typescript');
    });

    test('returns unknown for unrecognized file extensions and directories', () => {
        assert.strictEqual(classifyAppHostPath('/repo/apphost.py'), 'unknown');
        assert.strictEqual(classifyAppHostPath('/repo/apphost'), 'unknown');
    });

    test('classification is case-insensitive', () => {
        assert.strictEqual(classifyAppHostPath('APPHOST.CSPROJ'), 'csharp');
        assert.strictEqual(classifyAppHostPath('AppHost.TS'), 'typescript');
    });
});

suite('appHostLanguage.classifyAppHostDirectory', () => {
    // Each test creates a unique temp directory under the OS temp root and
    // tears it down after. We rely on real fs because classifyAppHostDirectory
    // uses readdirSync; mocking would defeat the purpose.
    const tempDirs: string[] = [];
    function makeTempDir(): string {
        const dir = mkdtempSync(join(tmpdir(), 'aspire-classify-'));
        tempDirs.push(dir);
        return dir;
    }
    teardown(() => {
        for (const dir of tempDirs) {
            if (existsSync(dir)) {
                rmSync(dir, { recursive: true, force: true });
            }
        }
        tempDirs.length = 0;
    });

    test('returns unknown for undefined or missing directory', () => {
        assert.strictEqual(classifyAppHostDirectory(undefined), 'unknown');
        assert.strictEqual(classifyAppHostDirectory(''), 'unknown');
        assert.strictEqual(classifyAppHostDirectory('/path/that/definitely/does/not/exist/aspire-test'), 'unknown');
    });

    test('classifies directory containing a .csproj as csharp', () => {
        const dir = makeTempDir();
        writeFileSync(join(dir, 'AppHost.csproj'), '');
        assert.strictEqual(classifyAppHostDirectory(dir), 'csharp');
    });

    test('classifies directory containing a .cs AppHost as csharp', () => {
        const dir = makeTempDir();
        writeFileSync(join(dir, 'AppHost.cs'), '');
        writeFileSync(join(dir, 'README.md'), '');
        assert.strictEqual(classifyAppHostDirectory(dir), 'csharp');
    });

    test('classifies directory containing an apphost.ts as typescript', () => {
        const dir = makeTempDir();
        writeFileSync(join(dir, 'apphost.ts'), '');
        writeFileSync(join(dir, 'package.json'), '{}');
        assert.strictEqual(classifyAppHostDirectory(dir), 'typescript');
    });

    test('classifies directory containing an apphost.cjs as typescript', () => {
        const dir = makeTempDir();
        writeFileSync(join(dir, 'apphost.cjs'), '');
        assert.strictEqual(classifyAppHostDirectory(dir), 'typescript');
    });

    test('returns unknown for a directory with no recognized AppHost markers', () => {
        const dir = makeTempDir();
        writeFileSync(join(dir, 'README.md'), '');
        writeFileSync(join(dir, 'main.py'), '');
        assert.strictEqual(classifyAppHostDirectory(dir), 'unknown');
    });

    test('returns csharp when both csharp and typescript markers exist (deterministic fallback)', () => {
        // Highly unusual but plausible during polyglot migration. The
        // classifier prefers csharp so the dimension stays deterministic
        // rather than depending on directory-listing order.
        const dir = makeTempDir();
        writeFileSync(join(dir, 'AppHost.csproj'), '');
        writeFileSync(join(dir, 'apphost.ts'), '');
        assert.strictEqual(classifyAppHostDirectory(dir), 'csharp');
    });
});
