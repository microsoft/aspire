import * as assert from 'assert';
import { Capability, getSupportedCapabilities } from '../capabilities';
import { addInteractionServiceEndpoints, IInteractionService } from '../server/interactionService';
import { ICliRpcClient } from '../server/rpcClient';

// The CLI compares against this exact literal (KnownCapabilities.AppHostLogOutput in
// src/Aspire.Cli/Utils/ExtensionHelper.cs). It is restated here rather than imported because
// the two sides ship from different languages and different feeds, which is precisely why the
// pairing needs a test instead of a compiler to hold it together.
const appHostLogOutputCapability = 'apphost-log-output.v1';

// This file lives apart from the other capability tests on purpose. It has to keep working
// across merges that rewrite neighbouring capability tokens, so it is scoped to the
// apphost-log-output family and touches no line another change is likely to own.
suite('AppHost log output capability', () => {
    test('advertises the versioned token and never an unversioned variant', () => {
        const advertised = getSupportedCapabilities() as string[];
        const family = advertised.filter(capability => capability.startsWith('apphost-log-output'));

        assert.deepStrictEqual(
            family,
            [appHostLogOutputCapability],
            `The extension must advertise exactly ['${appHostLogOutputCapability}'], but advertised ` +
            `[${family.map(capability => `'${capability}'`).join(', ')}].\n\n` +
            `An unversioned 'apphost-log-output' never matches the literal the CLI compares against, so the CLI ` +
            `falls back to the unstructured log path and AppHost logs are duplicated in the debug console again ` +
            `with no error anywhere. That silent-disagreement failure is the same shape as ` +
            `https://github.com/microsoft/aspire/issues/15850, where Aspire 13.2.0-13.2.4 advertised a token one ` +
            `side did not honor and users silently launched stale builds.\n\n` +
            `If you are resolving a merge conflict in getSupportedCapabilities(): keep the versioned token and ` +
            `drop any unversioned one. Capability tokens are versioned per feature in this repo, and "keep both ` +
            `sides" is the resolution that reintroduces the bug.`);
    });

    test('rejects an unversioned token at compile time', () => {
        // If an unversioned 'apphost-log-output' is ever added to the Capability union, this
        // directive becomes unused and compilation fails. That is the intent: the union should be
        // incapable of expressing the token, so the mistake cannot reach the runtime assertion above.
        // @ts-expect-error 'apphost-log-output' must never be a valid capability - use the versioned token.
        const unversioned: Capability = 'apphost-log-output';

        assert.strictEqual(unversioned, 'apphost-log-output');
    });

    test('registers the writeAppHostLogEntry endpoint that the advertised token promises', () => {
        const registeredMethods: string[] = [];
        const connection = {
            onRequest: (method: string) => {
                registeredMethods.push(method);
            }
        };

        // Registration only binds handlers, so any object shaped like a function bag is enough here.
        // A proxy keeps this test from breaking every time an unrelated endpoint is added.
        const interactionService = new Proxy({}, {
            get: () => () => undefined
        }) as IInteractionService;

        addInteractionServiceEndpoints(connection as any, interactionService, {} as ICliRpcClient, callback => callback);

        assert.ok(
            registeredMethods.includes('writeAppHostLogEntry'),
            `Advertising '${appHostLogOutputCapability}' promises the CLI that a 'writeAppHostLogEntry' RPC handler ` +
            `exists. Without it the CLI's first structured log record faults the RPC connection instead of falling ` +
            `back, so the token must be removed from getSupportedCapabilities() in the same change that removes the ` +
            `handler. Registered methods: [${registeredMethods.join(', ')}].`);
    });
});
