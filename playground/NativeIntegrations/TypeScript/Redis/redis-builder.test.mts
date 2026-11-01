// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

import assert from 'node:assert/strict';
import { createServer } from 'node:net';
import { join } from 'node:path';
import { setImmediate } from 'node:timers/promises';
import { test } from 'node:test';
import { createBuilder } from './redis-builder.mjs';
import { RedisContainerResource } from './redis-authoring.mjs';
import { ExecutableResource, NativeApplication } from './native-builder.mjs';

test('default volumes use stable application identity and portable cross-language vectors', async () => {
    const root = process.platform === 'win32' ? 'C:\\native-volume-tests\\shop' : '/native-volume-tests/shop';
    const options = { applicationRoot: root };
    const first = await createBuilder([], options);
    const cache = first.addRedis('cache').withDataVolume();
    const expected = process.platform === 'win32' ? 'aspire-shop-9e1a5d5bed7f2e30-cache-data' : 'aspire-shop-150be96b0b469209-cache-data';
    assert.equal(first.getDefaultDataVolumeName(cache.resource), expected);
    first.build();

    const rerun = await createBuilder([], options);
    assert.equal(rerun.getDefaultDataVolumeName(rerun.addRedis('cache').withDataVolume().resource), expected);
    const revision = await createBuilder([`--native-application-root=${join(root, '..', 'shop')}/`]);
    assert.equal(revision.getDefaultDataVolumeName(revision.addRedis('cache').withDataVolume().resource), expected);

    const otherRoot = process.platform === 'win32' ? 'C:\\native-volume-tests\\other\\shop' : '/native-volume-tests/other/shop';
    const other = await createBuilder([], { applicationRoot: otherRoot });
    assert.equal(other.getDefaultDataVolumeName(other.addRedis('cache').withDataVolume().resource),
        process.platform === 'win32' ? 'aspire-shop-66cbb558beb5aaf5-cache-data' : 'aspire-shop-7dd235e7275aa500-cache-data');

    const named = await createBuilder([], { applicationRoot: root, applicationName: 'Inventory / 世界' });
    const namedVolume = named.getDefaultDataVolumeName(named.addRedis('cache').withDataVolume().resource);
    assert.equal(namedVolume, process.platform === 'win32' ? 'aspire-inventory_____-9556ab04977d6d99-cache-data' : 'aspire-inventory_____-76d0c05eac46aedc-cache-data');
    assert.match(namedVolume, /^[a-zA-Z0-9][a-zA-Z0-9_.-]*$/);
    const selected = await createBuilder([`--native-application-root=${root}`, '--native-application-name=Inventory / 世界'],
        { applicationRoot: otherRoot, applicationName: 'ignored' });
    assert.equal(selected.getDefaultDataVolumeName(selected.addRedis('cache').resource), namedVolume);
    await assert.rejects(createBuilder([], { applicationRoot: '' }), /root cannot be empty/);
    await assert.rejects(createBuilder([], { applicationName: ' ' }), /name cannot be empty/);

    const explicitPrefix = await createBuilder(['--native-volume-prefix=User.Exact_Prefix'], { applicationRoot: otherRoot });
    assert.equal(explicitPrefix.getDefaultDataVolumeName(explicitPrefix.addRedis('cache').withDataVolume().resource), 'User.Exact_Prefix-cache-data');

    const defaults = await createBuilder([]);
    const launchRoot = await createBuilder([], { applicationRoot: process.cwd() });
    assert.equal(defaults.getDefaultDataVolumeName(defaults.addRedis('cache').resource),
        launchRoot.getDefaultDataVolumeName(launchRoot.addRedis('cache').resource));
});

test('familiar awaited fluent composition and build perform no RPC', async () => {
    let connections = 0;
    const listener = createServer(socket => { connections++; socket.destroy(); });
    await new Promise<void>(resolve => listener.listen(0, '127.0.0.1', resolve));
    try {
        const address = listener.address();
        assert(address && typeof address !== 'string');
        const builder = await createBuilder([`--native-endpoint=127.0.0.1:${address.port}`]);
        const cache = await builder.addRedis('cache').withDataVolume();
        const consumer = await builder.addExecutable('consumer', 'unused-executable', '.').withReference(cache).waitFor(cache);
        const application = builder.build();
        assert(cache.resource instanceof RedisContainerResource);
        assert(consumer.resource instanceof ExecutableResource);
        assert(application instanceof NativeApplication);
        assert.equal(cache.applicationBuilder, builder);
        assert.equal(consumer.applicationBuilder, builder);
        assert.equal('then' in cache, false);
        assert.deepEqual(builder.resources.map(resource => resource.name), ['cache-password', 'cache', 'consumer']);
        await setImmediate();
        assert.equal(connections, 0);
        assert.throws(() => cache.withDataVolume('after-build'), /sealed/);
        assert.throws(() => builder.addRedis('after-build'), /sealed/);
    } finally {
        await new Promise<void>((resolve, reject) => listener.close(error => error ? reject(error) : resolve()));
    }
});

test('cross-plan references and the unsupported legacy publishing argument fail explicitly', async () => {
    const first = await createBuilder([]);
    const second = await createBuilder([]);
    const cache = first.addRedis('cache');
    const consumer = second.addExecutable('consumer', 'unused-executable', '.');
    assert.throws(() => consumer.withReference(cache), /another authoring plan/);
    assert.throws(() => consumer.waitFor(cache), /another authoring plan/);
    await assert.rejects(createBuilder(['--publish=true']), /not supported/);
});

test('CLI publishing arguments select publishing without executing pipeline actions during composition', async () => {
    const builder = await createBuilder(['--operation', 'publish', '--step', 'deploy', '--output-path', './output']);
    assert.equal(builder.executionContext().isPublishMode(), true);
    assert.equal(builder.configuration.step, 'deploy');
    assert.equal(builder.configuration['output-path'], './output');
    let executions = 0;
    builder.pipeline().addStep('custom-publish', async () => { executions++; }, { requiredBy: ['publish'] });
    builder.build();
    assert.equal(executions, 0);
    assert.equal((await createBuilder(['--operation=inspect'])).executionContext().isPublishMode(), true);
    assert.equal((await createBuilder([])).executionContext().isPublishMode(), false);
});

test('pre-cancelled run acquires no native authority and disposal joins once', async () => {
    const builder = await createBuilder([]);
    const application = builder.build();
    await application.run(AbortSignal.abort());
    const first = application.dispose(), second = application.dispose();
    assert.equal(first, second);
    await first;
    await assert.rejects(application.start());
});
