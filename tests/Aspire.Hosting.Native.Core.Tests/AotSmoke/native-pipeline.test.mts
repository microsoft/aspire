// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

import assert from 'node:assert/strict';
import { test } from 'node:test';
import { NativePipeline, WellKnownPipelineSteps, WellKnownPipelineTags } from './native-pipeline.mjs';
import type { NativePipelineServer, NativePipelineStepEditor } from './native-pipeline.mjs';
import type { PublishingContext } from './native-builder.mjs';

interface GraphStep {
    name: string;
    dependsOn: string[];
    requiredBy: string[];
    tags: string[];
    resource: string | null;
}
/** Canned native claims intentionally do not implement a guest-side dependency scheduler. */
class Server implements NativePipelineServer {
    graph: { steps: GraphStep[]; mode?: 'run' | 'publish' } = { steps: [] };
    target = '';
    readonly completions: { step: string; status: string; failed: boolean }[] = [];
    readonly progress: { step: string; type: string; id: string; status: string; complete: boolean; failed: boolean; markdown: boolean }[] = [];
    constructor(private readonly batches: string[][] = []) {}
    async configurePublishing(graphJson: string): Promise<void> { this.graph = JSON.parse(graphJson); }
    async beginPublishing(target: string): Promise<void> { this.target = target; }
    async claimPublishingSteps(): Promise<string[]> { return this.batches.shift() ?? []; }
    async reportPublishingActivity(step: string, status: string, complete: boolean, failed: boolean): Promise<void> {
        assert.equal(complete, true);
        this.completions.push({ step, status, failed });
    }
    async reportPublishingProgress(step: string, type: string, id: string, status: string, complete: boolean, failed: boolean, markdown: boolean): Promise<void> {
        this.progress.push({ step, type, id, status, complete, failed, markdown });
    }
}
function context(signal = new AbortController().signal): PublishingContext {
    return { model: { schemaVersion: 1, parameters: [], resources: [] }, outputPath: '.', applicationDirectory: '.', environmentName: 'test', signal };
}
function deferred() {
    let resolve!: () => void;
    const promise = new Promise<void>(complete => { resolve = complete; });
    return { promise, resolve };
}

test('configuration matches current TypeScript pipeline shape and seals editors without executing actions', async () => {
    const pipeline = new NativePipeline();
    let actions = 0;
    let retainedEditor: NativePipelineStepEditor | undefined;
    await pipeline.addStep('compile', async () => { actions++; }, {
        dependsOn: [WellKnownPipelineSteps.BuildPrereq], requiredBy: [WellKnownPipelineSteps.Publish],
        tags: [WellKnownPipelineTags.BuildCompute], description: 'Compile sources', resource: 'web',
    });
    await pipeline.configure(async ctx => {
        const p = await ctx.pipeline();
        const steps = await p.steps();
        assert(steps.length > 1);
        const tagged = await ctx.getSteps(WellKnownPipelineTags.BuildCompute);
        assert.equal(tagged.length, 1);
        const step = tagged[0]!;
        retainedEditor = step;
        assert.equal(await step.name(), 'compile');
        assert.equal(await step.description(), 'Compile sources');
        assert.deepEqual(await step.dependsOnSteps(), ['build-prereq']);
        assert.deepEqual(await step.requiredBySteps(), ['publish']);
        await step.addTag('validated');
        await step.dependsOn('before-start');
        await step.requiredBy('deploy');
    });
    await pipeline.configure(async ctx => {
        assert.deepEqual(await Promise.all((await (await ctx.pipeline()).steps()).map(step => step.name())), ['compile']);
    }, 'web');
    await pipeline.resolve(context());
    assert.equal(actions, 0);
    assert(retainedEditor !== undefined);
    await assert.rejects((retainedEditor as NativePipelineStepEditor).addTag('late'), /sealed/);
    await assert.rejects(pipeline.addStep('late', async () => {}), /sealed/);
    const server = new Server();
    await pipeline.register(server);
    const step = server.graph.steps.find(step => step.name === 'compile')!;
    assert.equal(step.resource, 'web');
    assert.deepEqual(step.tags, ['build-compute', 'validated']);
    assert(step.dependsOn.includes('publish-prereq'));
    assert(step.dependsOn.includes('deploy-prereq'));
    assert(step.requiredBy.includes('build'));
    assert.equal(actions, 0);
});

test('native ready claims execute concurrently and newly ready callbacks follow native claims', async () => {
    const pipeline = new NativePipeline();
    const leftStarted = deferred(), rightStarted = deferred();
    const order: string[] = [];
    await pipeline.addStep('left', async () => { order.push('left'); leftStarted.resolve(); await rightStarted.promise; });
    await pipeline.addStep('right', async () => { order.push('right'); rightStarted.resolve(); await leftStarted.promise; });
    await pipeline.addStep('release', async () => { order.push('release'); });
    await pipeline.addStep('unrelated', async () => { assert.fail('Unselected callback ran'); });
    const server = new Server([['left', 'right'], ['release'], []]);
    await pipeline.resolve(context());
    await pipeline.register(server);
    await pipeline.execute(server, context(), 'release');
    assert.equal(server.target, 'release');
    assert.deepEqual(order, ['left', 'right', 'release']);
    assert.deepEqual(server.completions.map(result => [result.step, result.failed]), [['left', false], ['right', false], ['release', false]]);
});

test('failure cancels running callbacks, suppresses raw exception progress, and stops claims', async () => {
    const pipeline = new NativePipeline();
    const peerStarted = deferred();
    let peerCanceled = false;
    await pipeline.addStep('failure', async () => { await peerStarted.promise; throw new Error('secret=do-not-log'); });
    await pipeline.addStep('peer', async ctx => {
        peerStarted.resolve();
        await new Promise<void>(resolve => ctx.signal.addEventListener('abort', () => { peerCanceled = true; resolve(); }, { once: true }));
    });
    await pipeline.addStep('never', async () => { assert.fail('Failure must stop new claims'); });
    const server = new Server([['failure', 'peer'], ['never']]);
    await pipeline.resolve(context());
    await pipeline.register(server);
    await assert.rejects(pipeline.execute(server, context(), 'failure'), /do-not-log/);
    assert.equal(peerCanceled, true);
    assert.deepEqual(server.completions.map(result => [result.step, result.failed]), [['failure', true], ['peer', true]]);
    assert(server.completions.every(result => result.status === 'Pipeline step failed or canceled.'));
});

test('external cancellation reaches the step signal and waits for callbacks to settle', async () => {
    const pipeline = new NativePipeline();
    const controller = new AbortController();
    const started = deferred();
    let settled = false;
    await pipeline.addStep('wait', async ctx => {
        started.resolve();
        await new Promise<void>(resolve => ctx.signal.addEventListener('abort', () => { settled = true; resolve(); }, { once: true }));
    });
    const server = new Server([['wait']]);
    await pipeline.resolve(context());
    await pipeline.register(server);
    const execution = pipeline.execute(server, context(controller.signal), 'wait');
    await started.promise;
    controller.abort();
    await assert.rejects(execution, { name: 'AbortError' });
    assert.equal(settled, true);
    assert.equal(server.completions[0]!.failed, true);
});

test('step contexts expose model, summaries, safe logs, and task ownership through native progress', async () => {
    const pipeline = new NativePipeline();
    const publishing = context();
    await pipeline.addStep('report', async ctx => {
        assert.equal(ctx.publishingContext.model, publishing.model);
        assert.equal(ctx.signal, ctx.publishingContext.signal);
        assert.deepEqual(await (await ctx.model()).getResources(), []);
        assert.equal(await (await ctx.model()).findResourceByName('missing'), null);
        assert.equal(await ctx.executionContext().isPublishMode(), true);
        assert.equal(await ctx.executionContext().isRunMode(), false);
        assert.equal(await ctx.pipelineContext(), ctx);
        assert.equal(await ctx.cancellationToken(), ctx.signal);
        await assert.rejects(ctx.services(), /dependency injection/);
        await (await ctx.summary()).add('Artifact', 'compose.yaml');
        await (await ctx.summary()).addMarkdown('Result', '**Ready**');
        await (await ctx.logger()).logDebug('Safe diagnostic');
        const step = await ctx.reportingStep();
        const task = await step.createTask('Writing');
        await task.updateTask('Written');
        await task.completeTask({ completionMessage: 'Complete' });
        const markdownTask = await step.createMarkdownTask('**Starting**');
        await markdownTask.completeTaskMarkdown('**Warning**', { completionState: 'completed-with-warning' });
        await step.completeStep('Callback still owns step');
    });
    const server = new Server([['report']]);
    await pipeline.resolve(publishing);
    await pipeline.register(server);
    await pipeline.execute(server, publishing, 'report');
    assert.deepEqual(server.progress.map(item => [item.type, item.complete, item.markdown]), [
        ['summary', false, false], ['summary', false, true], ['log-debug', false, false],
        ['task', false, false], ['task', false, false], ['task', true, false],
        ['task', false, true], ['task-warning', true, true], ['log', false, false],
    ]);
    assert(server.progress.every(item => item.step === 'report'));
    assert.deepEqual(server.completions, [{ step: 'report', status: 'Completed.', failed: false }]);
});

test('unsupported operations and services fail clearly rather than silently succeeding', async () => {
    for (const operation of ['publish', 'deploy', 'push', 'destroy', 'check-container-runtime', 'diagnostics']) {
        const pipeline = new NativePipeline(), server = new Server([[operation]]);
        await pipeline.resolve(context());
        await pipeline.register(server);
        await assert.rejects(pipeline.execute(server, context(), operation), /integration-provided action/);
    }
});

test('custom push actions satisfy the push aggregator without a hardcoded verb sequence', async () => {
    const pipeline = new NativePipeline(), server = new Server([['image-push'], ['push']]);
    let invoked = false;
    await pipeline.addStep('image-push', async () => { invoked = true; }, { tags: [WellKnownPipelineTags.PushContainerImage] });
    await pipeline.resolve(context());
    await pipeline.register(server);
    await pipeline.execute(server, context(), 'push');
    assert.equal(invoked, true);
    assert(server.completions.every(result => !result.failed));
});

test('run startup uses the same generic pipeline and contexts with console diagnostics instead of a publishing reader', async t => {
    const pipeline = new NativePipeline(), server = new Server([['validate'], ['before-start']]);
    const publishing: PublishingContext = { ...context(), executionMode: 'run' };
    const messages: string[] = [];
    t.mock.method(console, 'info', (message: string) => { messages.push(message); });
    let validated = false;
    await pipeline.addStep('validate', async ctx => {
        assert.equal(await ctx.executionContext().isRunMode(), true);
        assert.equal(await ctx.executionContext().isPublishMode(), false);
        await (await ctx.logger()).logInformation('Validating startup');
        await (await ctx.summary()).add('Startup', 'Ready');
        validated = true;
    }, { requiredBy: [WellKnownPipelineSteps.BeforeStart] });
    await pipeline.addStep('publish-only', async () => { assert.fail('Run must not execute publishing'); }, {
        requiredBy: [WellKnownPipelineSteps.Publish],
    });
    await pipeline.resolve(publishing);
    await pipeline.register(server);
    assert.equal(server.graph.mode, 'run');
    await pipeline.execute(server, publishing, WellKnownPipelineSteps.BeforeStart);
    assert.equal(validated, true);
    assert.deepEqual(server.completions.map(item => item.step), ['validate', 'before-start']);
    assert.deepEqual(messages, [
        '[pipeline:validate] Starting.',
        '[pipeline:validate] Validating startup',
        '[pipeline:validate] Startup: Ready',
        '[pipeline:validate] Completed.',
        '[pipeline:before-start] Starting.',
        '[pipeline:before-start] Completed.',
    ]);
});

test('failed reporting tasks fail their callback instead of allowing a successful terminal step', async () => {
    const pipeline = new NativePipeline(), server = new Server([['report']]);
    await pipeline.addStep('report', async ctx => {
        const task = await (await ctx.reportingStep()).createTask('Upload');
        await task.completeTask({ completionState: 'completed-with-error' });
        assert.fail('Failed task must stop its callback');
    });
    await pipeline.resolve(context());
    await pipeline.register(server);
    await assert.rejects(pipeline.execute(server, context(), 'report'), /Reporting task completed with an error/);
    assert.equal(server.progress[1]!.failed, true);
    assert.deepEqual(server.completions, [{ step: 'report', status: 'Pipeline step failed or canceled.', failed: true }]);
});

test('only user step additions or configuration mark a pipeline customized', async () => {
    const pipeline = new NativePipeline();
    assert.equal(pipeline.hasCustomizations(), false);
    await pipeline.addStep('custom', async () => {});
    assert.equal(pipeline.hasCustomizations(), true);
    const configured = new NativePipeline();
    await configured.configure(async () => {});
    assert.equal(configured.hasCustomizations(), true);
    const resourceConfigured = new NativePipeline();
    await resourceConfigured.configure(async () => {}, 'web');
    assert.equal(resourceConfigured.hasCustomizations(), true);
    const defaults = new NativePipeline();
    await defaults.resolve(context());
    assert.equal(defaults.hasCustomizations(), false);
});
