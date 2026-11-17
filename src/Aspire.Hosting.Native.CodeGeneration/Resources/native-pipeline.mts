// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

import type { PublishingContext } from './native-builder.mjs';

export const WellKnownPipelineSteps = {
    Publish: 'publish', PublishPrereq: 'publish-prereq', Deploy: 'deploy', DeployPrereq: 'deploy-prereq',
    ProcessParameters: 'process-parameters', Build: 'build', BuildPrereq: 'build-prereq',
    Push: 'push', PushPrereq: 'push-prereq', Diagnostics: 'diagnostics',
    ValidateComputeEnvironments: 'validate-compute-environments', BeforeStart: 'before-start',
    CheckContainerRuntime: 'check-container-runtime', Destroy: 'destroy', DestroyPrereq: 'destroy-prereq',
} as const;
export const WellKnownPipelineTags = {
    ProvisionInfrastructure: 'provision-infra', BuildCompute: 'build-compute',
    PushContainerImage: 'push-container-image', DeployCompute: 'deploy-compute',
} as const;

/** Structural subset of the generated native server; no guest-side execution graph is authoritative. */
export interface NativePipelineServer {
    configurePublishing(graphJson: string): PromiseLike<unknown>;
    beginPublishing(target: string): PromiseLike<unknown>;
    claimPublishingSteps(): PromiseLike<string[]>;
    reportPublishingActivity(step: string, status: string, complete: boolean, failed: boolean): PromiseLike<unknown>;
    reportPublishingProgress(step: string, type: string, id: string, status: string, complete: boolean, failed: boolean, markdown: boolean): PromiseLike<unknown>;
}
export interface NativePipelineStepOptions {
    dependsOn?: string[];
    requiredBy?: string[];
    tags?: string[];
    description?: string;
    resource?: string;
}
export type NativePipelineAction = (context: NativePipelineStepContext) => Promise<void>;
interface Step {
    name: string;
    action: NativePipelineAction;
    dependsOn: string[];
    requiredBy: string[];
    tags: string[];
    description: string | null;
    resource: string | null;
}

export class NativePipelineStepEditor {
    constructor(private readonly step: Step, private readonly mutable: () => void) {}
    async name(): Promise<string> { return this.step.name; }
    async description(): Promise<string | null> { return this.step.description; }
    async dependsOnSteps(): Promise<string[]> { return [...this.step.dependsOn]; }
    async requiredBySteps(): Promise<string[]> { return [...this.step.requiredBy]; }
    async tags(): Promise<string[]> { return [...this.step.tags]; }
    async addTag(tag: string): Promise<void> { this.mutable(); this.step.tags.push(tag); }
    async dependsOn(name: string): Promise<void> { this.mutable(); this.step.dependsOn.push(name); }
    async requiredBy(name: string): Promise<void> { this.mutable(); this.step.requiredBy.push(name); }
}

/** A configuration view can be scoped to the resource whose callback is being evaluated. */
export class NativePipelineView {
    constructor(private readonly source: Step[], private readonly mutable: () => void, private readonly resource?: string) {}
    async steps(): Promise<NativePipelineStepEditor[]> {
        return this.source.filter(step => this.resource === undefined || step.resource === this.resource)
            .map(step => new NativePipelineStepEditor(step, this.mutable));
    }
    async stepsByTag(tag: string): Promise<NativePipelineStepEditor[]> {
        const steps = await this.steps();
        const selected: NativePipelineStepEditor[] = [];
        for (const step of steps) if ((await step.tags()).includes(tag)) selected.push(step);
        return selected;
    }
}
export class NativePipelineConfigurationContext {
    constructor(private readonly view: NativePipelineView) {}
    async pipeline(): Promise<NativePipelineView> { return this.view; }
    async getSteps(tag: string): Promise<NativePipelineStepEditor[]> { return this.view.stepsByTag(tag); }
    async log(): Promise<never> { throw new Error('Native pipeline configuration logging is unavailable; report progress from a pipeline step.'); }
    async services(): Promise<never> { return unsupportedServices(); }
}
function unsupportedServices(): never {
    throw new Error('Native pipelines do not provide managed dependency injection services. Use the native publishing model and explicit integration APIs.');
}
type CompletionOptions = { completionMessage?: string; completionState?: 'completed' | 'completed-with-warning' | 'completed-with-error' };

export class NativePipelineStepContext {
    readonly signal: AbortSignal;
    private nextTask = 0;
    constructor(
        readonly publishingContext: PublishingContext,
        private readonly server: NativePipelineServer,
        private readonly step: string,
    ) { this.signal = publishingContext.signal; }
    async model() {
        const resources = this.publishingContext.model.resources;
        return {
            getResources: async () => [...resources],
            findResourceByName: async (name: string) => resources.find(resource => resource.name === name) ?? null,
        };
    }
    async pipelineContext(): Promise<NativePipelineStepContext> { return this; }
    executionContext() {
        return {
            isPublishMode: async () => this.publishingContext.executionMode !== 'run',
            isRunMode: async () => this.publishingContext.executionMode === 'run',
        };
    }
    async cancellationToken(): Promise<AbortSignal> { return this.signal; }
    async services(): Promise<never> { return unsupportedServices(); }
    async summary() {
        return {
            add: async (key: string, value: string) => this.progress('summary', key, value, false, false, false),
            addMarkdown: async (key: string, value: string) => this.progress('summary', key, value, false, false, true),
        };
    }
    async logger() {
        // Never serialize exceptions or arbitrary objects. The publishing boundary redacts known secrets.
        const log = async (level: string, text: string) => this.progress(level, this.step, text, false, false, false);
        return {
            logInformation: async (text: string) => log('log', text),
            logDebug: async (text: string) => log('log-debug', text),
            logWarning: async (text: string) => log('log-warning', text),
            logError: async (text: string) => log('log-error', text),
            logTrace: async (text: string) => log('log-trace', text),
            logCritical: async (text: string) => log('log-critical', text),
        };
    }
    async reportingStep() {
        const create = async (text: string, markdown: boolean) => {
            const id = `${this.step}:task:${++this.nextTask}`;
            await this.progress('task', id, text, false, false, markdown);
            const finish = async (message: string, options: CompletionOptions, markdown: boolean) => {
                await this.progress(options.completionState === 'completed-with-warning' ? 'task-warning' : 'task',
                    id, message, true, options.completionState === 'completed-with-error', markdown);
                if (options.completionState === 'completed-with-error')
                    throw new Error('Reporting task completed with an error.');
            };
            return {
                updateTask: async (text: string) => this.progress('task', id, text, false, false, false),
                updateTaskMarkdown: async (text: string) => this.progress('task', id, text, false, false, true),
                completeTask: async (options: CompletionOptions = {}) => finish(options.completionMessage ?? 'Completed.', options, false),
                completeTaskMarkdown: async (text: string, options: CompletionOptions = {}) => finish(text, options, true),
            };
        };
        const log = async (level: string, text: string, markdown: boolean) => {
            const types: Record<string, string> = {
                information: 'log', debug: 'log-debug', warning: 'log-warning', error: 'log-error',
                trace: 'log-trace', critical: 'log-critical',
            };
            const type = types[level.toLowerCase()];
            if (!type) throw new Error(`Unsupported native pipeline log level '${level}'.`);
            await this.progress(type, this.step, text, false, false, markdown);
        };
        // Only the scheduler completes native steps after the callback has returned. Early completion
        // here must not release dependent steps while the action is still running.
        const complete = async (text: string, options: CompletionOptions, markdown: boolean) => {
            if (options.completionState === 'completed-with-error') throw new Error('Reporting step completed with an error.');
            if (options.completionState === 'completed-with-warning')
                await this.progress('step-warning', this.step, text, false, false, markdown);
            await log('information', text, markdown);
        };
        return {
            createTask: async (text: string) => create(text, false),
            createMarkdownTask: async (text: string) => create(text, true),
            logStep: async (level: string, text: string) => log(level, text, false),
            logStepMarkdown: async (level: string, text: string) => log(level, text, true),
            completeStep: async (text: string, options: CompletionOptions = {}) => complete(text, options, false),
            completeStepMarkdown: async (text: string, options: CompletionOptions = {}) => complete(text, options, true),
        };
    }
    private async progress(type: string, id: string, text: string, complete: boolean, failed: boolean, markdown: boolean): Promise<void> {
        if (typeof text !== 'string' || typeof id !== 'string' || text.length > 4096 || id.length > 4096)
            throw new Error('Native pipeline progress requires strings of at most 4096 characters.');
        await this.server.reportPublishingProgress(this.step, type, id, text, complete, failed, markdown);
        if (this.publishingContext.executionMode === 'run') {
            const message = `[pipeline:${this.step}] ${type === 'summary' ? `${id}: ` : ''}${text}`;
            if (failed || type === 'log-error' || type === 'log-critical') console.error(message);
            else if (type === 'task-warning' || type === 'step-warning' || type === 'log-warning') console.warn(message);
            else console.info(message);
        }
    }
}

/** Records actions and configuration; the native server seals, validates, selects, and schedules the DAG. */
export class NativePipeline {
    private readonly recorded: Step[] = [];
    private readonly configurations: { callback: (context: NativePipelineConfigurationContext) => Promise<void>; resource?: string }[] = [];
    private state: 'recording' | 'resolving' | 'resolved' | 'failed' = 'recording';
    private registered = false;
    private executionMode: 'run' | 'publish' = 'publish';
    private customized = false;
    constructor() {
        const noop: NativePipelineAction = async () => {};
        const unsupported = (name: string): NativePipelineAction => async () => {
            throw new Error(`Native pipeline '${name}' requires an integration-provided action.`);
        };
        const add = (name: string, action: NativePipelineAction, dependsOn: string[] = []) => {
            this.recorded.push({ name, action, dependsOn, requiredBy: [], tags: [], description: `Native ${name} pipeline step.`, resource: null });
        };
        add('process-parameters', noop);
        for (const operation of ['build', 'publish', 'deploy', 'push', 'destroy']) {
            add(`${operation}-prereq`, noop, ['process-parameters']);
            add(operation, operation !== 'build' ? async () => {
                const aggregation = this.recorded.find(step => step.name === operation)!;
                if (!this.recorded.some(step => step.requiredBy.includes(operation)) &&
                    !aggregation.dependsOn.some(name => name !== `${operation}-prereq`))
                    throw new Error(`Native pipeline '${operation}' requires an integration-provided action.`);
            } : noop, [`${operation}-prereq`]);
        }
        add('before-start', noop, ['validate-compute-environments']);
        add('validate-compute-environments', noop);
        add('check-container-runtime', unsupported('check-container-runtime'));
        add('diagnostics', unsupported('diagnostics'));
    }
    hasCustomizations(): boolean { return this.customized; }
    async addStep(name: string, action: NativePipelineAction, options: NativePipelineStepOptions = {}): Promise<void> {
        this.mutable();
        this.customized = true;
        this.recorded.push({
            name, action, dependsOn: [...(options.dependsOn ?? [])], requiredBy: [...(options.requiredBy ?? [])],
            tags: [...(options.tags ?? [])], description: options.description ?? null, resource: options.resource ?? null,
        });
    }
    async configure(callback: (context: NativePipelineConfigurationContext) => Promise<void>, resource?: string): Promise<void> {
        this.mutable();
        this.customized = true;
        this.configurations.push({ callback, resource });
    }
    async resolve(context: PublishingContext): Promise<void> {
        if (this.state !== 'recording') throw new Error('Native pipeline can only be resolved once.');
        this.state = 'resolving';
        try {
            this.executionMode = context.executionMode ?? 'publish';
            context.signal.throwIfAborted();
            for (const config of this.configurations) {
                await config.callback(new NativePipelineConfigurationContext(new NativePipelineView(this.recorded, () => this.mutable(), config.resource)));
                context.signal.throwIfAborted();
            }
            // Tagged compute steps join the standard aggregators and run after their prerequisites.
            for (const step of this.recorded) {
                const operations = [
                    ...(step.tags.includes('build-compute') ? ['build'] : []),
                    ...(step.tags.includes('push-container-image') ? ['push'] : []),
                    ...(step.tags.includes('deploy-compute') || step.tags.includes('provision-infra') ? ['deploy'] : []),
                ];
                for (const operation of operations) {
                    step.dependsOn.push(`${operation}-prereq`);
                    step.requiredBy.push(operation);
                }
                // Explicit requiredBy provides the same prerequisite boundary as tagged factories.
                for (const operation of ['build', 'publish', 'deploy', 'push', 'destroy'])
                    if (step.requiredBy.includes(operation)) step.dependsOn.push(`${operation}-prereq`);
            }
            this.state = 'resolved';
        } catch (error) { this.state = 'failed'; throw error; }
    }
    async register(server: NativePipelineServer): Promise<void> {
        if (this.state !== 'resolved' || this.registered) throw new Error('Resolve the native pipeline exactly once before registration.');
        await server.configurePublishing(JSON.stringify({
            mode: this.executionMode,
            steps: this.recorded.map(({ action: _action, ...graph }) => graph),
        }));
        this.registered = true;
    }
    async execute(server: NativePipelineServer, context: PublishingContext, target: string): Promise<void> {
        if (!this.registered) throw new Error('Register the native pipeline before execution.');
        if ((context.executionMode ?? 'publish') !== this.executionMode)
            throw new Error('Pipeline execution mode must match its resolved graph.');
        await server.beginPublishing(target);
        const controller = new AbortController();
        const cancel = () => controller.abort(context.signal.reason);
        context.signal.addEventListener('abort', cancel, { once: true });
        if (context.signal.aborted) cancel();
        const running = new Map<string, Promise<void>>();
        let failure: unknown;
        const actions = new Map(this.recorded.map(step => [step.name, step.action]));
        const run = async (name: string) => {
            try {
                controller.signal.throwIfAborted();
                const action = actions.get(name);
                if (!action) throw new Error(`Native server claimed unregistered action '${name}'.`);
                if (this.executionMode === 'run') console.info(`[pipeline:${name}] Starting.`);
                await action(new NativePipelineStepContext({ ...context, signal: controller.signal }, server, name));
                controller.signal.throwIfAborted();
                await server.reportPublishingActivity(name, 'Completed.', true, false);
                if (this.executionMode === 'run') console.info(`[pipeline:${name}] Completed.`);
            } catch (error) {
                failure ??= error;
                controller.abort();
                // Raw exceptions can contain parameter values, credentials, or process output.
                await server.reportPublishingActivity(name, 'Pipeline step failed or canceled.', true, true);
                if (this.executionMode === 'run') console.error(`[pipeline:${name}] Pipeline step failed or canceled.`);
            } finally { running.delete(name); }
        };
        try {
            while (true) {
                if (!controller.signal.aborted) {
                    for (const name of await server.claimPublishingSteps()) {
                        const task = run(name);
                        running.set(name, task);
                    }
                }
                if (running.size === 0) break;
                await Promise.race(running.values());
            }
            if (failure !== undefined) throw failure;
            controller.signal.throwIfAborted();
        } finally {
            context.signal.removeEventListener('abort', cancel);
            controller.abort();
            // Even when transport reporting fails, never leave callback actions running past lifecycle completion.
            await Promise.allSettled(running.values());
        }
    }
    private mutable(): void {
        if (this.state !== 'recording' && this.state !== 'resolving') throw new Error('The native pipeline is sealed.');
    }
}
