import * as assert from 'assert';
import * as fs from 'fs';
import * as path from 'path';

import {
    __resetAppHostIdentityRegistryForTests,
    bindCurrentAppHostTarget,
    getOrCreateIdentityForCurrentAppHostTarget,
} from '../utils/appHostIdentity';
import { ScriptedRealpath } from './helpers/scriptedRealpath';
import { createFixtureDirectory } from './helpers/editorAssistanceTestSupport';

suite('AppHost identity binding', () => {
    let workspaceRoot: string;
    let firstAppHostPath: string;
    let secondAppHostPath: string;
    let aliasAppHostPath: string;
    let scriptedRealpath: ScriptedRealpath | undefined;

    setup(() => {
        __resetAppHostIdentityRegistryForTests();
        workspaceRoot = createFixtureDirectory('identity-binding');
        firstAppHostPath = path.join(workspaceRoot, 'First', 'AppHost.csproj');
        secondAppHostPath = path.join(workspaceRoot, 'Second', 'AppHost.csproj');
        aliasAppHostPath = path.join(workspaceRoot, 'Alias', 'AppHost.csproj');
        for (const appHostPath of [firstAppHostPath, secondAppHostPath, aliasAppHostPath]) {
            fs.mkdirSync(path.dirname(appHostPath), { recursive: true });
            fs.writeFileSync(appHostPath, '<Project />');
        }
    });

    teardown(() => {
        scriptedRealpath?.restore();
        scriptedRealpath = undefined;
        __resetAppHostIdentityRegistryForTests();
        fs.rmSync(workspaceRoot, { recursive: true, force: true });
    });

    /**
     * Asserts the only two outcomes a binding may produce: the identity belongs to the physical
     * path that was captured with it, or the binding failed closed onto an identity no
     * revalidation can match.
     */
    function assertBindingCorrespondsOrFailsClosed(binding: { identity: string; canonicalPath: string }): void {
        const canonicalIdentity = getOrCreateIdentityForCurrentAppHostTarget(binding.canonicalPath);
        if (binding.identity === canonicalIdentity) {
            return;
        }

        assert.notStrictEqual(
            binding.identity,
            getOrCreateIdentityForCurrentAppHostTarget(firstAppHostPath),
            'A binding that did not fail closed must not carry another AppHost\'s identity.');
        assert.notStrictEqual(
            binding.identity,
            getOrCreateIdentityForCurrentAppHostTarget(secondAppHostPath),
            'A binding that did not fail closed must not carry another AppHost\'s identity.');
    }

    test('derives the identity from the canonical path it captured when a selector churns A to B and back', () => {
        // The canonical samples that bracket the identity read both observe A while the identity
        // read itself observes B. A binding that samples the selector twice therefore looks
        // stable and still pairs B's identity with A's path, which is exactly the confusion the
        // binding exists to prevent: reads and launches would run against A while everything
        // that checks freshness answers for B.
        scriptedRealpath = new ScriptedRealpath();
        scriptedRealpath.script(aliasAppHostPath, {
            results: [
                firstAppHostPath,
                secondAppHostPath,
                secondAppHostPath,
                secondAppHostPath,
                secondAppHostPath,
                firstAppHostPath,
            ],
            thereafter: firstAppHostPath,
        });

        const binding = bindCurrentAppHostTarget(aliasAppHostPath);

        scriptedRealpath.restore();
        scriptedRealpath = undefined;
        assert.strictEqual(
            binding.identity,
            getOrCreateIdentityForCurrentAppHostTarget(binding.canonicalPath),
            'The identity must belong to the canonical path the binding captured.');
        assertBindingCorrespondsOrFailsClosed(binding);
    });

    test('derives the identity from the canonical path it captured when a selector churns B to A and back', () => {
        scriptedRealpath = new ScriptedRealpath();
        scriptedRealpath.script(aliasAppHostPath, {
            results: [
                secondAppHostPath,
                firstAppHostPath,
                firstAppHostPath,
                firstAppHostPath,
                firstAppHostPath,
                secondAppHostPath,
            ],
            thereafter: secondAppHostPath,
        });

        const binding = bindCurrentAppHostTarget(aliasAppHostPath);

        scriptedRealpath.restore();
        scriptedRealpath = undefined;
        assert.strictEqual(
            binding.identity,
            getOrCreateIdentityForCurrentAppHostTarget(binding.canonicalPath),
            'The identity must belong to the canonical path the binding captured.');
        assertBindingCorrespondsOrFailsClosed(binding);
    });

    test('binds the canonical path the identity was taken from under sustained selector churn', () => {
        // Every sample sees a different file, so no pair of samples can agree. The binding has
        // to end on one physical path with an identity that either belongs to it or belongs to
        // nothing at all - never on B's identity holding A's path.
        const churn: string[] = [];
        for (let index = 0; index < 64; index++) {
            churn.push(index % 2 === 0 ? firstAppHostPath : secondAppHostPath);
        }

        scriptedRealpath = new ScriptedRealpath();
        scriptedRealpath.script(aliasAppHostPath, { results: churn, thereafter: firstAppHostPath });

        const binding = bindCurrentAppHostTarget(aliasAppHostPath);

        scriptedRealpath.restore();
        scriptedRealpath = undefined;
        assertBindingCorrespondsOrFailsClosed(binding);
    });

    test('fails closed onto an unmatched identity when the selector never settles', () => {
        // A target nothing can pin down must not inherit either candidate's identity: the next
        // revalidation has to refuse it rather than publish or launch anything.
        let index = 0;
        const alternating: string[] = [];
        while (index < 256) {
            alternating.push(index % 2 === 0 ? firstAppHostPath : secondAppHostPath);
            index++;
        }

        scriptedRealpath = new ScriptedRealpath();
        scriptedRealpath.script(aliasAppHostPath, { results: alternating });

        const binding = bindCurrentAppHostTarget(aliasAppHostPath);
        const firstIdentity = getOrCreateIdentityForCurrentAppHostTarget(firstAppHostPath);
        const secondIdentity = getOrCreateIdentityForCurrentAppHostTarget(secondAppHostPath);

        assert.notStrictEqual(binding.identity, firstIdentity);
        assert.notStrictEqual(binding.identity, secondIdentity);
    });

    test('normalizes the absolute path it falls back to when the filesystem cannot canonicalize it', () => {
        // The fallback path becomes the path every later operation and every reservation key is
        // built from, so an unnormalized spelling would key one missing AppHost two ways.
        // `path.join` normalizes, so the unnormalized spelling is assembled by hand.
        const missingAppHost = path.join(workspaceRoot, 'Missing', 'AppHost.csproj');
        const unnormalizedMissingAppHost =
            `${workspaceRoot}${path.sep}Missing${path.sep}..${path.sep}Missing${path.sep}.${path.sep}AppHost.csproj`;

        const binding = bindCurrentAppHostTarget(unnormalizedMissingAppHost);

        assert.strictEqual(binding.canonicalPath, missingAppHost);
    });

    test('keeps one identity for repeated bindings of a settled selector', () => {
        const first = bindCurrentAppHostTarget(firstAppHostPath);
        const second = bindCurrentAppHostTarget(firstAppHostPath);

        assert.strictEqual(first.identity, second.identity);
        assert.strictEqual(first.canonicalPath, fs.realpathSync.native(firstAppHostPath));
        assert.strictEqual(second.canonicalPath, first.canonicalPath);
    });
});
