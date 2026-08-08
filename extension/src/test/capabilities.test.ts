import * as assert from 'assert';

import { getSupportedCapabilities } from '../capabilities';

suite('Capabilities', () => {
    test('AppHost build ownership advertises only the v2 capability', () => {
        // Typed as readonly string[] rather than Capabilities so the filter below can observe a
        // token that is no longer in the Capability union. That matters because the failure this
        // guards against arrives through a merge, not through someone hand-editing this file.
        const capabilities: readonly string[] = getSupportedCapabilities();

        assert.deepStrictEqual(
            capabilities.filter(capability => capability.startsWith('build-dotnet-using-cli')),
            ['build-dotnet-using-cli.v2'],
            'The extension must advertise the versioned build-ownership token and only that token. '
            + 'Advertising the unversioned "build-dotnet-using-cli" tells a CLI that honors only it '
            + 'that the extension has ceded the pre-build, and CLI 13.2.0-13.2.4 then skipped that '
            + 'build on no-debug launches, so nobody built and the user launched stale output '
            + '(https://github.com/microsoft/aspire/issues/15850). If this failed right after a '
            + 'merge, drop the unversioned token from getSupportedCapabilities in capabilities.ts.');
    });
});
