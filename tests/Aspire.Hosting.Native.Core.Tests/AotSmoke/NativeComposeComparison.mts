// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

import { readFile } from 'node:fs/promises';
import { generateDockerCompose, resolveDockerComposeInputs } from './docker-compose.mjs';
import type { PublishingContext } from './native-builder.mjs';

const request = JSON.parse(await readFile(process.argv[2]!, 'utf8')) as {
    context: PublishingContext; name: string; environment: Record<string, string>;
};
const plan = generateDockerCompose(request.context, request.name);
process.stdout.write(JSON.stringify({
    compose: plan.compose, inputs: plan.inputs, services: plan.services,
    resolved: resolveDockerComposeInputs(plan, request.context.model, request.environment),
}));
