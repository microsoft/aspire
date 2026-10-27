// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

import { createBuilder } from './redis-builder.mjs';
import { CapabilityError } from './transport.mjs';

async function compose(operation: string, password: string) {
    const builder = await createBuilder();
    const credential = builder.addParameterFromValue('cache-password', password, { secret: true });
    const cache = await builder.addRedis('cache', credential).withDataVolume(builder.configuration['data-volume']!);
    const consumer = await builder.addExecutable('consumer', process.execPath, process.cwd())
        .withArgs(builder.configuration['consumer-path']!, '--redis-consume', operation)
        .withReference(cache)
        .waitFor(cache);
    return { builder, consumer };
}

const initial = await compose('write', 'real-authoring-password');
const application = initial.builder.build();
try {
    await application.start();
    await application.waitForCompletion(initial.consumer);
    const rejected = await compose('read', 'real-authoring-password');
    rejected.builder.addExecutable('invalid', 'not-an-absolute-executable', '.');
    try {
        await application.update(rejected.builder);
        throw new Error('Invalid native declarations must not succeed.');
    } catch (error) {
        if (!(error instanceof CapabilityError) || error.code !== 'INVALID_ARGUMENT') throw error;
    }
    await application.waitForCompletion(initial.consumer);
    const next = await compose('read', 'real-authoring-password-rotated');
    await application.update(next.builder);
    await application.waitForCompletion(next.consumer);
} finally {
    await application.dispose();
}
console.log('redis-persistence-ok');
