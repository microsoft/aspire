// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

import { ReferenceExpression, WellKnownPipelineSteps, WellKnownPipelineTags, createBuilder } from './.aspire/modules/aspire.mjs';
import assert from 'node:assert/strict';
import { randomUUID } from 'node:crypto';
import { mkdir, readFile, writeFile } from 'node:fs/promises';
import { join } from 'node:path';
import { setTimeout as delay } from 'node:timers/promises';
import { fileURLToPath } from 'node:url';

const builder = await createBuilder();
const compose = await builder.addDockerComposeEnvironment('compose');
await compose.withDashboard({ enabled: false });
const password = await builder.addParameter('cache-password', { secret: true });
const cache = builder.addContainer('cache', 'redis:7.4-alpine')
    .withEnvironment('REDIS_PASSWORD', password)
    .withEntrypoint('/bin/sh')
    .withArgs('-c', 'exec redis-server --requirepass "$REDIS_PASSWORD" --appendonly yes --appendfsync always --dir /data')
    .withEndpoint('tcp', 6379, 'redis');
await cache.withComputeEnvironment(compose);
cache.withVolume(builder.getDefaultDataVolumeName(cache.resource), '/data');
cache.withConnectionString(ReferenceExpression.create('redis://:', password.asReference().encodeUri(), '@',
    cache.getEndpoint('tcp').hostAndPort));
await cache.publishAsDockerComposeService(async (_target, service) => {
    await service.healthcheck.set({ test: ['CMD-SHELL', 'REDISCLI_AUTH="$REDIS_PASSWORD" redis-cli ping | grep -q PONG'],
        interval: '1s', timeout: '3s', retries: 30 });
    await service.restart.set('unless-stopped');
});
const web = builder.addExecutable('web', process.execPath, fileURLToPath(new URL('./web/', import.meta.url)))
    .withArgs('server.mts')
    .withReference(cache)
    .withHttpEndpoint(builder.executionContext().isPublishMode()
        ? { name: 'http', targetPort: 8080 }
        : { name: 'http', env: 'PORT' })
    .withExternalHttpEndpoints()
    .waitFor(cache);
await web.withComputeEnvironment(compose);
await web.publishAsDockerComposeService(async (_target, service) => {
    await service.image.set(process.env.ASPIRE_COMPOSE_SAMPLE_IMAGE ?? 'aspire-v2-compose-web:local');
    await service.build.set({ context: './web', dockerfile: './web/Dockerfile' });
    await service.healthcheck.set({ test: ['CMD', 'node', '-e', "fetch('http://127.0.0.1:8080/health').then(r=>process.exit(r.ok?0:1)).catch(()=>process.exit(1))"],
        interval: '1s', timeout: '3s', retries: 30 });
});

// These policies consume Compose publishing steps and artifacts, which do not
// exist during local execution.
if (builder.executionContext().isPublishMode()) {
    const pipeline = await builder.pipeline();
    const invocation = randomUUID();
    for (const branch of ['left', 'right']) {
        await pipeline.addStep(`policy-${branch}`, async context => {
            const directory = join(context.publishingContext.outputPath, 'pipeline');
            await mkdir(directory, { recursive: true });
            await writeFile(join(directory, `${branch}.started`), invocation);
            const other = branch === 'left' ? 'right' : 'left';
            const deadline = Date.now() + 10_000;
            for (;;) {
                try {
                    if (await readFile(join(directory, `${other}.started`), 'utf8') === invocation) break;
                    if (Date.now() >= deadline) throw new Error('Ready pipeline branches must execute concurrently.');
                    await delay(25, undefined, { signal: context.signal });
                }
                catch (error) {
                    if (!(error instanceof Error && 'code' in error && error.code === 'ENOENT')) throw error;
                    if (Date.now() >= deadline) throw new Error('Ready pipeline branches must execute concurrently.');
                    await delay(25, undefined, { signal: context.signal });
                }
            }
            const reporting = await context.reportingStep();
            const task = await reporting.createTask(`Validate ${branch} deployment policy`);
            await task.updateTask('Both independent branches started.');
            await task.completeTask({ completionMessage: 'Policy validated.' });
            await (await context.summary()).add(`Policy ${branch}`, 'Validated');
            await writeFile(join(directory, `${branch}.complete`), invocation);
        }, { dependsOn: ['validate-compose'], requiredBy: ['publish-compose'], tags: [WellKnownPipelineTags.BuildCompute] });
    }
    await cache.withPipelineStepFactory('cache-policy', async context => {
        const model = await context.model();
        assert.ok(await model.findResourceByName('cache'));
        await (await context.summary()).add('Cache policy', 'Native model inspected');
    }, { dependsOn: [WellKnownPipelineSteps.Build], requiredBy: [WellKnownPipelineSteps.Publish], tags: ['sample-policy'] });
    await cache.withPipelineConfiguration(async context => {
        const steps = await context.getSteps('sample-policy');
        assert.equal(steps.length, 1);
        for (const step of steps) await step.addTag('validated');
    });
    await pipeline.addStep('verify-publish', async context => {
        const directory = join(context.publishingContext.outputPath, 'pipeline');
        for (const branch of ['left', 'right']) assert.equal(await readFile(join(directory, `${branch}.complete`), 'utf8'), invocation);
        const document = JSON.parse(await readFile(join(context.publishingContext.outputPath, 'compose.yaml'), 'utf8')) as {
            services: Record<string, { image: string }>;
        };
        assert.equal(document.services.cache!.image, 'redis:7.4-alpine');
        await writeFile(join(directory, 'custom-target.complete'), '');
    }, { dependsOn: [WellKnownPipelineSteps.Publish], description: 'Verify generated artifacts after publishing.' });
}
await builder.build().run();
