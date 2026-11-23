// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

import { createBuilder } from './redis-builder.mjs';

const builder = await createBuilder();
const cache = await builder.addRedis('cache').withDataVolume();
await builder.addExecutable('consumer', process.execPath, process.cwd())
    .withArgs(builder.configuration['consumer-path']!, '--redis-consume', builder.configuration.operation ?? 'write')
    .withReference(cache)
    .waitFor(cache);
await builder.build().run();
