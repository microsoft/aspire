// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

import { spawn } from 'node:child_process';
import { mkdir, readFile, stat, writeFile } from 'node:fs/promises';
import { isIP } from 'node:net';
import { devNull } from 'node:os';
import { basename, isAbsolute, relative, resolve, sep } from 'node:path';
import {
    ComputeEnvironmentResource, DistributedApplicationBuilder, ParameterResource, ReferenceExpression, ResourceBuilder,
} from './native-builder.mjs';
import type {
    EndpointReference, PublishExpression, PublishModel, PublishResource, PublishingContext, Resource,
} from './native-builder.mjs';
import { WellKnownPipelineSteps } from './native-pipeline.mjs';

export interface DockerComposeServiceOptions {
    /** Both paths are relative to applicationDirectory. Contexts remain absolute local paths in the artifact. */
    build?: { context: string; dockerfile: string };
    /** Executables require both an explicit image and build metadata; the run executable is never translated. */
    image?: string;
    healthcheck?: { test: string[]; interval?: string; timeout?: string; retries?: number };
    /** Overrides native external-endpoint selection. Omitted hostIp uses loopback; omitted published requests a random port. */
    ports?: { endpoint: string; published?: number; hostIp?: string }[];
    exclude?: boolean;
    /** Explicitly discards host lifecycle hooks in favor of the required target healthcheck. */
    replaceRunLifecycle?: boolean;
}

export interface ComposeService {
    image: string;
    build?: { context: string; dockerfile: string };
    entrypoint?: string[];
    command?: string[];
    environment?: Record<string, string>;
    expose?: string[];
    ports?: { target: number; published?: string; host_ip: string; protocol: string }[];
    volumes?: { type: 'volume' | 'bind'; source: string; target: string; read_only: boolean }[];
    depends_on?: Record<string, { condition: 'service_started' | 'service_healthy' }>;
    healthcheck?: { test: string[]; interval?: string; timeout?: string; retries?: number };
    labels: Record<string, string>;
    container_name?: string;
    restart?: string;
    pull_policy?: string;
    configs?: ComposeJson[];
    secrets?: ComposeJson[];
    ulimits?: Record<string, ComposeJson>;
    networks?: string[];
}

export type ComposeJson = string | number | boolean | null | ComposeJson[] | { [key: string]: ComposeJson };
export type ComposeRecord = { [key: string]: ComposeJson };
export interface ComposeDocument {
    name: string;
    services: Record<string, ComposeService>;
    volumes?: Record<string, ComposeRecord>;
    networks?: Record<string, ComposeRecord>;
    configs?: Record<string, ComposeRecord>;
    secrets?: Record<string, ComposeRecord>;
}

export interface DeploymentInputs {
    schemaVersion: 1;
    projectName: string;
    parameters: { name: string; environmentVariable: string }[];
    /** Complete symbolic expressions, never their resolved secret values. */
    variables: Record<string, PublishExpression>;
    environment?: Record<string, { description: string | null; defaultValue?: string; secret: boolean }>;
}

export interface DockerComposePlan {
    compose: ComposeDocument;
    inputs: DeploymentInputs;
    services: { resource: string; service: string; healthcheck: boolean;
        ports: { endpoint: string; target: number; protocol: string; scheme: string; hostIp: string; published?: number }[] }[];
}

export interface DockerCommandOptions {
    cwd: string;
    env: Readonly<Record<string, string | undefined>>;
    signal: AbortSignal;
    timeoutMs: number;
}

export interface DockerCommandResult { exitCode: number; stdout: string; stderr: string }
export type DockerCommandRunner = (
    executable: string, arguments_: readonly string[], options: DockerCommandOptions,
) => Promise<DockerCommandResult>;

export interface DockerComposeEnvironmentOptions {
    runner?: DockerCommandRunner;
    waitTimeoutSeconds?: number;
    /** Test seam. Production resolves only explicit ASPIRE_PARAMETER_* entries from process.env. */
    parameterEnvironment?: Readonly<Record<string, string | undefined>>;
}

export interface DockerComposeDeploymentState {
    schemaVersion: 1;
    projectName: string;
    services: { resource: string; service: string; state: 'running'; health: 'healthy' | 'not-configured';
        endpoints: { name: string; hostIp: string; port: number; url: string }[] }[];
}

/** Records only publish metadata, preserving the resource's local launch/lifecycle behavior. */
export function publishAsDockerComposeService<T extends Resource>(
    resourceBuilder: ResourceBuilder<T>, options: DockerComposeServiceOptions | DockerComposeServiceCallback,
): ResourceBuilder<T> {
    if (!resourceBuilder.applicationBuilder.executionContext().isPublishMode()) {
        return resourceBuilder;
    }
    ensureComposeInfrastructure(resourceBuilder.applicationBuilder);
    if (typeof options === 'function') {
        const callbacks = serviceCallbacks.get(resourceBuilder.resource) ?? [];
        callbacks.push(options);
        serviceCallbacks.set(resourceBuilder.resource, callbacks);
        return resourceBuilder;
    }
    validateOptions(options);
    return resourceBuilder.withPublishConfiguration('dockerCompose', JSON.stringify(options));
}

const compare = (left: string, right: string): number => left < right ? -1 : left > right ? 1 : 0;
const sorted = <T,>(entries: readonly T[], key: (entry: T) => string): T[] =>
    [...entries].sort((left, right) => compare(key(left), key(right)));
const record = <T,>(): Record<string, T> => Object.create(null) as Record<string, T>;
const literal = (value: string): string => value.replaceAll('$', () => '$$');
const json = (value: unknown): string => `${JSON.stringify(value, null, 2)}\n`;

/** Synchronous accessors are also await-compatible, like today's generated TypeScript SDK. */
export class ComposeProperty<T> {
    constructor(private readonly read: () => T, private readonly write: (value: T) => void) {}
    get(): T { return this.read(); }
    set(value: T): void { this.write(value); }
}

export class ComposeReadOnlyDictionary<T> {
    constructor(protected readonly entries: Record<string, T>) {}
    get(name: string): T {
        const value = this.entries[name];
        if (value === undefined) {
            throw new Error(`Compose dictionary entry '${name}' does not exist.`);
        }
        return value;
    }
    count(): number { return Object.keys(this.entries).length; }
}
export class ComposeDictionary<T> extends ComposeReadOnlyDictionary<T> {
    constructor(entries: Record<string, T>, private readonly transform: (value: T) => T = value => value) { super(entries); }
    set(name: string, value: T): void { this.entries[name] = this.transform(value); }
}

function composeJson(value: ComposeJson): ComposeJson {
    if (typeof value === 'string') { return literal(value); }
    if (value === null || typeof value === 'boolean' || (typeof value === 'number' && Number.isFinite(value))) { return value; }
    if (Array.isArray(value)) { return value.map(composeJson); }
    object(value, 'Compose JSON value');
    return Object.fromEntries(Object.entries(value).map(([key, entry]) => [key, composeJson(entry)]));
}

function composeKey(value: string, description: string): void {
    if (!/^[a-zA-Z0-9][a-zA-Z0-9_.-]*$/.test(value)) {
        throw new Error(`${description} must contain letters, digits, dots, underscores or hyphens.`);
    }
}

interface EnvironmentConfiguration {
    defaultNetworkName: string | null;
    dashboardEnabled: boolean;
    dashboardHostPort: number | null;
    dashboardForwardedHeaders: boolean;
}
type Configure<T> = (value: T) => void | Promise<void>;
export type DockerComposeServiceCallback = (resource: DockerComposeServiceResource, service: ComposeServiceBuilder) => void | Promise<void>;
const serviceCallbacks = new WeakMap<Resource, DockerComposeServiceCallback[]>();
const infrastructure = new WeakSet<DistributedApplicationBuilder>();
const environmentTargets = new WeakMap<DockerComposeEnvironmentResource, DockerComposeEnvironment>();

function ensureComposeInfrastructure(builder: DistributedApplicationBuilder): void {
    if (infrastructure.has(builder)) { return; }
    infrastructure.add(builder);
    builder.pipeline().addStep('validate-docker-compose', async ctx => {
        const resources = ctx.publishingContext.model.resources;
        if (!resources.some(resource => resource.type === 'docker-compose/environment') &&
            (resources.some(resource => resource.configuration['publish.dockerCompose'] !== undefined) ||
                builder.resources.some(resource => serviceCallbacks.has(resource)))) {
            throw new Error('A resource uses publishAsDockerComposeService(), but no Docker Compose environment exists. Call builder.addDockerComposeEnvironment(name).');
        }
    }, { requiredBy: [WellKnownPipelineSteps.BeforeStart, WellKnownPipelineSteps.PublishPrereq, WellKnownPipelineSteps.DeployPrereq] });
}

export class DockerComposeEnvironmentResource extends ComputeEnvironmentResource {
    readonly configuration: EnvironmentConfiguration = {
        defaultNetworkName: null, dashboardEnabled: true, dashboardHostPort: null, dashboardForwardedHeaders: false,
    };
    readonly fileCallbacks: Configure<ComposeFileBuilder>[] = [];
    readonly envCallbacks: Configure<ComposeReadOnlyDictionary<CapturedEnvironmentVariable>>[] = [];
    constructor(name: string) { super(name, 'docker-compose/environment'); }
}

export class DockerComposeServiceResource {
    constructor(private readonly resourceName: string, private readonly environment: DockerComposeEnvironmentBuilder,
        private readonly plan: DockerComposePlan, private readonly model: PublishModel) {}
    name(): string { return this.resourceName; }
    parent(): DockerComposeEnvironmentBuilder { return this.environment; }
    parameter(parameter: ResourceBuilder<ParameterResource>): string {
        if (parameter.applicationBuilder !== this.environment.applicationBuilder) {
            throw new Error('A Compose placeholder must reference a parameter from the same application builder.');
        }
        const name = parameter.resource.name;
        if (!this.model.parameters.includes(name)) {
            throw new Error(`Compose customization references undeclared parameter '${name}'.`);
        }
        const environmentVariable = dockerComposeParameterVariable(name);
        const collision = this.plan.inputs.parameters.find(entry => entry.environmentVariable === environmentVariable && entry.name !== name);
        if (collision) {
            throw new Error('Parameter names collide after environment-variable normalization.');
        }
        if (!this.plan.inputs.parameters.some(entry => entry.name === name)) {
            this.plan.inputs.parameters.push({ name, environmentVariable });
            this.plan.inputs.parameters.sort((a, b) => compare(a.name, b.name));
        }
        return `\${${environmentVariable}:?Required deployment input ${environmentVariable}}`;
    }
}

export class ComposeServiceBuilder {
    readonly image: ComposeProperty<string | null>;
    readonly build: ComposeProperty<{ context: string; dockerfile: string } | null>;
    readonly healthcheck: ComposeProperty<NonNullable<DockerComposeServiceOptions['healthcheck']> | null>;
    readonly containerName: ComposeProperty<string | null>;
    readonly restart: ComposeProperty<string | null>;
    readonly pullPolicy: ComposeProperty<string | null>;
    readonly configs: { count(): number };
    readonly secrets: { count(): number };
    readonly ulimits: { count(): number };
    readonly labels: ComposeDictionary<string>;
    constructor(readonly model: ComposeService, render: (value: string) => string = literal, applicationDirectory?: string) {
        this.image = new ComposeProperty(() => model.image || null, value => {
            if (value === null) { model.image = ''; } else { validateOptions({ image: value }); model.image = literal(value); }
        });
        this.build = new ComposeProperty(() => model.build ?? null, value => {
            if (value === null) { delete model.build; } else {
                validateOptions({ build: value });
                if (!applicationDirectory) { throw new Error('Build customization requires a publishing applicationDirectory.'); }
                model.build = buildPaths(value, applicationDirectory);
            }
        });
        this.healthcheck = new ComposeProperty(() => model.healthcheck ?? null, value => {
            if (value === null) { delete model.healthcheck; } else {
                validateOptions({ healthcheck: value });
                model.healthcheck = { ...value, test: value.test.map(literal) };
            }
        });
        this.containerName = new ComposeProperty(() => model.container_name ?? null, value => {
            if (value === null) { delete model.container_name; } else { text(value, 'Container name'); model.container_name = render(value); }
        });
        this.restart = new ComposeProperty(() => model.restart ?? null, value => {
            if (value === null) { delete model.restart; } else {
                if (!/^(no|always|unless-stopped|on-failure(?::[1-9][0-9]*)?)$/.test(value)) {
                    throw new Error('Unsupported Compose restart policy.');
                }
                model.restart = value;
            }
        });
        this.pullPolicy = new ComposeProperty(() => model.pull_policy ?? null, value => {
            if (value === null) { delete model.pull_policy; } else {
                if (!/^(always|never|missing|build|daily|weekly|every_[0-9]+[hms])$/.test(value)) {
                    throw new Error('Unsupported Compose pull policy.');
                }
                model.pull_policy = value;
            }
        });
        this.configs = { count: () => model.configs?.length ?? 0 };
        this.secrets = { count: () => model.secrets?.length ?? 0 };
        this.ulimits = { count: () => Object.keys(model.ulimits ?? {}).length };
        this.labels = new ComposeDictionary(model.labels, literal);
    }
    addVolume(source: string, target: string, options: { isReadOnly?: boolean } = {}): this {
        composeKey(source, 'Volume source'); text(target, 'Volume target');
        object(options, 'Volume attachment'); keys(options, ['isReadOnly'], 'Volume attachment');
        if (options.isReadOnly !== undefined && typeof options.isReadOnly !== 'boolean') {
            throw new Error('Volume isReadOnly must be a boolean.');
        }
        if (!target.startsWith('/')) {
            throw new Error('Compose volume target must be absolute.');
        }
        (this.model.volumes ??= []).push({ type: 'volume', source, target: literal(target), read_only: options.isReadOnly ?? false });
        return this;
    }
}

export class ComposeFileBuilder {
    readonly name: ComposeProperty<string | null>;
    readonly services: ComposeReadOnlyDictionary<ComposeServiceBuilder>;
    private readonly serviceModels: Record<string, ComposeServiceBuilder>;
    constructor(readonly model: ComposeDocument, private readonly applicationDirectory?: string) {
        this.name = new ComposeProperty<string | null>(() => model.name, value => {
            if (value === null || !/^[a-z0-9][a-z0-9_-]*$/.test(value)) {
                throw new Error('Compose project name must contain lowercase letters, digits, hyphens or underscores.');
            }
            model.name = value;
        });
        this.serviceModels = Object.fromEntries(
            Object.entries(model.services).map(([name, service]) => [name, new ComposeServiceBuilder(service, literal, applicationDirectory)]));
        this.services = new ComposeReadOnlyDictionary(this.serviceModels);
    }
    addNetwork(name: string, options: ComposeRecord): this {
        composeKey(name, 'Network name'); object(options, 'Network options');
        (this.model.networks ??= record())[name] = Object.fromEntries(
            Object.entries(options).map(([key, value]) => [key, composeJson(value)])); return this;
    }
    addService(name: string, options: { image: string; environment?: Record<string, string>; restart?: string }): ComposeServiceBuilder {
        object(options, 'Compose service');
        keys(options, ['image', 'environment', 'restart'], 'Compose service');
        if (!/^[a-zA-Z0-9][a-zA-Z0-9_.-]*$/.test(name) || this.model.services[name]) {
            throw new Error(`Invalid or duplicate Compose service '${name}'.`);
        }
        validateOptions({ image: options.image });
        const model: ComposeService = { image: literal(options.image), labels: {} };
        if (options.environment) { model.environment = Object.fromEntries(Object.entries(options.environment).map(([k, v]) => [k, literal(v)])); }
        const service = new ComposeServiceBuilder(model, literal, this.applicationDirectory);
        if (options.restart !== undefined) { service.restart.set(options.restart); }
        this.model.services[name] = model; this.serviceModels[name] = service; return service;
    }
    async addVolume(name: string, options: { driver?: string; configure?: Configure<{ labels: ComposeDictionary<string> }> }): Promise<void> {
        object(options, 'Compose volume');
        keys(options, ['driver', 'configure'], 'Compose volume');
        composeKey(name, 'Volume name');
        const labels: Record<string, string> = record();
        const volume: ComposeRecord = options.driver === undefined ? {} : { driver: literal(options.driver) };
        if (options.configure) { await options.configure({ labels: new ComposeDictionary(labels, literal) }); }
        if (Object.keys(labels).length) { volume.labels = labels; }
        (this.model.volumes ??= record())[name] = volume;
    }
    addConfig(name: string, options: { content: string }): void {
        object(options, 'Compose config'); keys(options, ['content'], 'Compose config');
        composeKey(name, 'Config name'); (this.model.configs ??= record())[name] = { content: literal(options.content) };
    }
    addSecret(name: string, options: { external: true; name?: string }): void {
        object(options, 'Compose secret'); keys(options, ['external', 'name'], 'Compose secret');
        composeKey(name, 'Secret name');
        if (options.external !== true) {
            throw new Error('Compose secrets must be external; plaintext secret artifacts are not supported.');
        }
        (this.model.secrets ??= record())[name] = { external: true, ...(options.name === undefined ? {} : { name: literal(options.name) }) };
    }
}

export class CapturedEnvironmentVariable {
    readonly description: ComposeProperty<string | null>;
    readonly defaultValue: ComposeProperty<string | null>;
    constructor(private readonly entry: { description: string | null; defaultValue?: string; secret: boolean }) {
        this.description = new ComposeProperty(() => entry.description, value => { entry.description = value; });
        this.defaultValue = new ComposeProperty(() => entry.defaultValue ?? null, value => {
            if (entry.secret && value !== null) {
                throw new Error('Secret defaults cannot be written to deployment artifacts; supply the explicit deployment input.');
            }
            if (value === null) { delete entry.defaultValue; } else { text(value, 'Environment default', true); entry.defaultValue = value; }
        });
    }
}

export class ComposeDashboardBuilder {
    constructor(private readonly environment: DockerComposeEnvironmentBuilder) {}
    name(): string { return `${this.environment.name()}-dashboard`; }
    withHostPort(options: { port?: number | null } = {}): this {
        if (options.port !== undefined && options.port !== null) { port(options.port, 'Dashboard host port'); }
        this.environment.resource.configuration.dashboardHostPort = options.port ?? null;
        this.environment.save(); return this;
    }
    withForwardedHeaders(options: { enabled: boolean } = { enabled: true }): this {
        this.environment.resource.configuration.dashboardForwardedHeaders = options.enabled; this.environment.save(); return this;
    }
    primaryEndpoint(): { url(): string; host(): string; port(): number } {
        const value = this.environment.resource.configuration.dashboardHostPort;
        const host = value === null ? serviceName(this.name()) : '127.0.0.1';
        return { url: () => `http://${host}:${value ?? 18888}`, host: () => host, port: () => value ?? 18888 };
    }
    otlpGrpcEndpoint(): { url(): string; host(): string; port(): number } {
        return { url: () => `http://${serviceName(this.name())}:18889`, host: () => serviceName(this.name()), port: () => 18889 };
    }
}

export class DockerComposeEnvironmentBuilder extends ResourceBuilder<DockerComposeEnvironmentResource> {
    readonly defaultNetworkName = new ComposeProperty(() => this.resource.configuration.defaultNetworkName, value => {
        if (value !== null) { text(value, 'Default network name'); }
        this.resource.configuration.defaultNetworkName = value; this.save();
    });
    readonly dashboardEnabled = new ComposeProperty(() => this.resource.configuration.dashboardEnabled, value => {
        if (typeof value !== 'boolean') { throw new Error('dashboardEnabled must be a boolean.'); }
        this.resource.configuration.dashboardEnabled = value; this.save();
    });
    name(): string { return this.resource.name; }
    getHostAddressExpression(endpoint: EndpointReference): ComposeHostAddressExpression {
        if (!this.applicationBuilder.resources.includes(endpoint.resource)) {
            throw new Error('Endpoint belongs to another application builder.');
        }
        return new ComposeHostAddressExpression(serviceName(endpoint.resource.name));
    }
    save(): void {
        if (this.applicationBuilder.executionContext().isPublishMode()) {
            this.withPublishConfiguration('dockerComposeEnvironment', JSON.stringify(this.resource.configuration));
        }
    }
    withRunner(runner: DockerCommandRunner): this {
        if (this.applicationBuilder.executionContext().isPublishMode()) {
            const target = environmentTargets.get(this.resource);
            if (!target) { throw new Error('Register the Compose environment before configuring its Docker runner.'); }
            target.withRunner(runner);
        }
        return this;
    }
    async withProperties(configure: Configure<DockerComposeEnvironmentBuilder>): Promise<this> { await configure(this); return this; }
    configureComposeFile(configure: Configure<ComposeFileBuilder>): this { this.resource.fileCallbacks.push(configure); return this; }
    configureEnvFile(configure: Configure<ComposeReadOnlyDictionary<CapturedEnvironmentVariable>>): this { this.resource.envCallbacks.push(configure); return this; }
    withDashboard(options: { enabled?: boolean } = {}): this { this.dashboardEnabled.set(options.enabled ?? true); return this; }
    async configureDashboard(configure: Configure<ComposeDashboardBuilder>): Promise<this> {
        this.dashboardEnabled.set(true); await configure(new ComposeDashboardBuilder(this)); return this;
    }
}

export class ComposeHostAddressExpression extends ReferenceExpression {
    constructor(private readonly host: string) {
        super(context => context.value(ReferenceExpression.literal(host)));
    }
    getValue(): string { return this.host; }
}

declare module './native-builder.mjs' {
    interface DistributedApplicationBuilder {
        addDockerComposeEnvironment(name: string): DockerComposeEnvironmentBuilder;
    }
    interface ResourceBuilder<T extends Resource> {
        publishAsDockerComposeService(options: DockerComposeServiceOptions | DockerComposeServiceCallback): this;
        asEnvironmentPlaceholder(this: ResourceBuilder<ParameterResource>, service: DockerComposeServiceResource): string;
    }
}
DistributedApplicationBuilder.prototype.addDockerComposeEnvironment = function(name: string): DockerComposeEnvironmentBuilder {
    return addDockerComposeEnvironment(this, name);
};
ResourceBuilder.prototype.publishAsDockerComposeService = function<T extends Resource>(
    this: ResourceBuilder<T>, options: DockerComposeServiceOptions | DockerComposeServiceCallback,
): ResourceBuilder<T> {
    publishAsDockerComposeService(this, options); return this;
};
ResourceBuilder.prototype.asEnvironmentPlaceholder = function(
    this: ResourceBuilder<ParameterResource>, service: DockerComposeServiceResource,
): string {
    if (!(this.resource instanceof ParameterResource)) { throw new Error('Only parameters can become Compose environment placeholders.'); }
    return service.parameter(this);
};

/** Registers native graph data and ordinary pipeline steps, never a singleton deployment target. */
export function addDockerComposeEnvironment(builder: DistributedApplicationBuilder, name: string): DockerComposeEnvironmentBuilder {
    text(name, 'Docker Compose environment name');
    const resource = new DockerComposeEnvironmentResource(name);
    builder.addComputeEnvironment(resource);
    const result = new DockerComposeEnvironmentBuilder(builder, resource);
    result.save();
    if (!builder.executionContext().isPublishMode()) {
        return result;
    }
    ensureComposeInfrastructure(builder);
    const target = new DockerComposeEnvironment(name, {}, result);
    environmentTargets.set(resource, target);
    const pipeline = builder.pipeline();
    pipeline.addStep(`validate-${name}`, async ctx => { await target.plan(ctx.publishingContext); }, {
        requiredBy: [WellKnownPipelineSteps.BeforeStart, WellKnownPipelineSteps.PublishPrereq, WellKnownPipelineSteps.DeployPrereq],
        resource: name,
    });
    pipeline.addStep(`prepare-${name}`, async ctx => { await target.plan(ctx.publishingContext); }, {
        dependsOn: [`validate-${name}`], requiredBy: [`publish-${name}`], resource: name,
    });
    pipeline.addStep(`publish-${name}`, async ctx => { await target.publish(ctx.publishingContext); }, {
        dependsOn: [`prepare-${name}`], requiredBy: [WellKnownPipelineSteps.Publish], resource: name,
    });
    pipeline.addStep(`deploy-${name}`, async ctx => { await target.deploy(ctx.publishingContext); }, {
        dependsOn: [`publish-${name}`], requiredBy: [WellKnownPipelineSteps.Deploy], resource: name,
    });
    return result;
}

function text(value: unknown, description: string, allowEmpty = false): asserts value is string {
    if (typeof value !== 'string' || (!allowEmpty && value.trim().length === 0) || value.includes('\0')) {
        throw new Error(`${description} must be a ${allowEmpty ? '' : 'nonempty '}string without NUL characters.`);
    }
}

function object(value: unknown, description: string): asserts value is Record<string, unknown> {
    if (value === null || typeof value !== 'object' || Array.isArray(value)) {
        throw new Error(`${description} must be an object.`);
    }
}

function keys(value: Record<string, unknown>, allowed: readonly string[], description: string): void {
    if (Object.keys(value).some(key => !allowed.includes(key))) {
        throw new Error(`${description} contains unsupported options.`);
    }
}

function port(value: number, description: string): void {
    if (!Number.isInteger(value) || value < 1 || value > 65535) {
        throw new Error(`${description} must be between 1 and 65535.`);
    }
}

function validateOptions(value: unknown): asserts value is DockerComposeServiceOptions {
    object(value, 'Docker Compose configuration');
    keys(value, ['build', 'image', 'healthcheck', 'ports', 'exclude', 'replaceRunLifecycle'], 'Docker Compose configuration');
    for (const name of ['exclude', 'replaceRunLifecycle']) {
        if (value[name] !== undefined && typeof value[name] !== 'boolean') {
            throw new Error(`${name} must be a boolean.`);
        }
    }
    if (value.image !== undefined) {
        text(value.image, 'Deployment image');
        if (!/^[a-zA-Z0-9][a-zA-Z0-9._/:@-]*$/.test(value.image)) {
            throw new Error('Deployment image must be a literal image reference.');
        }
    }
    if (value.build !== undefined) {
        object(value.build, 'Build configuration');
        keys(value.build, ['context', 'dockerfile'], 'Build configuration');
        text(value.build.context, 'Build context');
        text(value.build.dockerfile, 'Dockerfile');
    }
    if (value.healthcheck !== undefined) {
        object(value.healthcheck, 'Healthcheck');
        keys(value.healthcheck, ['test', 'interval', 'timeout', 'retries'], 'Healthcheck');
        const check = value.healthcheck;
        if (!Array.isArray(check.test) || check.test.length < 2 || !['CMD', 'CMD-SHELL'].includes(check.test[0])) {
            throw new Error('Healthcheck test must start with CMD or CMD-SHELL and contain a command.');
        }
        check.test.forEach(value => text(value, 'Healthcheck command', true));
        if (check.test[0] === 'CMD-SHELL' && check.test.length !== 2) {
            throw new Error('CMD-SHELL healthcheck requires exactly one shell command.');
        }
        for (const key of ['interval', 'timeout']) {
            if (check[key] !== undefined && (typeof check[key] !== 'string' ||
                !/^(?:[0-9]+(?:\.[0-9]+)?(?:h|ms|us|s|m))+$/.test(check[key] as string) ||
                !/[1-9]/.test(check[key] as string))) {
                throw new Error(`Healthcheck ${key} must be a positive Compose duration.`);
            }
        }
        if (check.retries !== undefined && (!Number.isInteger(check.retries) || (check.retries as number) < 1)) {
            throw new Error('Healthcheck retries must be a positive integer.');
        }
    }
    if (value.ports !== undefined) {
        if (!Array.isArray(value.ports)) {
            throw new Error('Ports must be an array.');
        }
        const endpoints = new Set<string>();
        for (const item of value.ports) {
            object(item, 'Port exposure');
            keys(item, ['endpoint', 'published', 'hostIp'], 'Port exposure');
            text(item.endpoint, 'Port endpoint');
            if (endpoints.has(item.endpoint)) {
                throw new Error('An endpoint can be exposed only once.');
            }
            endpoints.add(item.endpoint);
            if (item.published !== undefined) {
                port(item.published as number, 'Published port');
            }
            if (item.hostIp !== undefined && (typeof item.hostIp !== 'string' || isIP(item.hostIp) === 0)) {
                throw new Error('Host IP must be an IPv4 or IPv6 address.');
            }
        }
    }
}

// FNV-1a is a non-security identity suffix, not a credential or integrity check.
function identity(value: string): string {
    let hash = 14695981039346656037n;
    for (const byte of Buffer.from(value, 'utf8')) {
        hash = BigInt.asUintN(64, (hash ^ BigInt(byte)) * 1099511628211n);
    }
    return hash.toString(16).padStart(16, '0');
}

function slug(value: string): string {
    return value.toLowerCase().replace(/[^a-z0-9-]+/g, '-').replace(/^-+|-+$/g, '').slice(0, 35) || 'resource';
}

function serviceName(value: string): string {
    return /^[a-z0-9][a-z0-9-]{0,62}$/.test(value) ? value : `${slug(value)}-${identity(value)}`;
}

export function dockerComposeProjectName(applicationDirectory: string, environment: string, name: string): string {
    text(environment, 'Deployment environment');
    text(name, 'Docker Compose environment name');
    const directory = resolve(applicationDirectory);
    return `aspire-${slug(basename(directory)).slice(0, 16)}-${slug(name).slice(0, 12)}-${slug(environment).slice(0, 12)}-${identity(json([directory, environment, name]))}`;
}

export function dockerComposeParameterVariable(name: string): string {
    text(name, 'Parameter name');
    return `ASPIRE_PARAMETER_${name.toUpperCase().replace(/[^A-Z0-9_]/g, '_')}`;
}

function optionsFor(resource: PublishResource): DockerComposeServiceOptions {
    const source = resource.configuration['publish.dockerCompose'];
    if (source === undefined) {
        return {};
    }
    let parsed: unknown;
    try {
        parsed = JSON.parse(source);
    } catch {
        throw new Error(`Resource '${resource.name}' has invalid Docker Compose JSON.`);
    }
    validateOptions(parsed);
    return parsed;
}

function buildPaths(build: { context: string; dockerfile: string }, applicationDirectory: string): { context: string; dockerfile: string } {
    const contextPath = resolve(applicationDirectory, build.context);
    const dockerfilePath = resolve(applicationDirectory, build.dockerfile);
    const within = relative(contextPath, dockerfilePath);
    if (within === '' || within === '..' || within.startsWith(`..${sep}`) || isAbsolute(within)) {
        throw new Error('Dockerfile must be inside its explicit build context.');
    }
    return { context: literal(contextPath), dockerfile: literal(dockerfilePath) };
}

function endpointValue(expression: Extract<PublishExpression, { kind: 'endpoint' }>,
    resources: ReadonlyMap<string, PublishResource>, names: ReadonlyMap<string, string>): string {
    const resource = resources.get(expression.resource);
    const host = names.get(expression.resource);
    if (!resource || !host) {
        throw new Error(`Endpoint reference targets missing or excluded service '${expression.resource}'; references across compute environments are not supported.`);
    }
    const endpoint = resource.endpoints.find(endpoint => endpoint.name === expression.endpoint);
    if (!endpoint) {
        throw new Error(`Endpoint '${expression.endpoint}' is missing on '${expression.resource}'.`);
    }
    const target = String(endpoint.targetPort);
    switch (expression.property) {
        case 'Host': return host;
        case 'Port': return target;
        case 'HostAndPort': return `${host}:${target}`;
        case 'Scheme': return endpoint.scheme;
        case 'Url': return `${endpoint.scheme}://${host}:${target}`;
        default: throw new Error('Unsupported endpoint property.');
    }
}

function validateDependencies(resources: ReadonlyMap<string, PublishResource>): void {
    const visited = new Set<string>(), active = new Set<string>();
    const visit = (resource: PublishResource): void => {
        if (active.has(resource.name)) {
            throw new Error(`Compose wait dependency cycle includes '${resource.name}'.`);
        }
        if (visited.has(resource.name)) {
            return;
        }
        active.add(resource.name);
        for (const name of resource.dependencies) {
            const dependency = resources.get(name);
            if (!dependency) {
                throw new Error(`Wait dependency targets missing or excluded service '${name}'; dependencies across compute environments are not supported.`);
            }
            visit(dependency);
        }
        active.delete(resource.name);
        visited.add(resource.name);
    };
    for (const resource of resources.values()) {
        visit(resource);
    }
}

/** Pure, deterministic generation. No Docker calls, parameter lookups, filesystem writes, or run-state reads. */
export function generateDockerCompose(
    context: Pick<PublishingContext, 'model' | 'applicationDirectory' | 'environmentName'>, name: string,
): DockerComposePlan {
    return createDockerCompose(context, name, new Set());
}

function createDockerCompose(
    context: Pick<PublishingContext, 'model' | 'applicationDirectory' | 'environmentName'>, name: string,
    customizable: ReadonlySet<string>,
): DockerComposePlan {
    if (context.model.schemaVersion !== 1) {
        throw new Error('Unsupported deployment model schema.');
    }
    const projectName = dockerComposeProjectName(context.applicationDirectory, context.environmentName, name);
    const resources = new Map<string, PublishResource>(), names = new Map<string, string>();
    const options = new Map<string, DockerComposeServiceOptions>(), allNames = new Set<string>();
    const environments = context.model.resources.filter(resource => resource.type === 'docker-compose/environment');
    const environmentResource = environments.find(resource => resource.name === name && resource.type === 'docker-compose/environment');
    if (environments.length && !environmentResource) {
        throw new Error(`Docker Compose environment '${name}' is not present in the native graph.`);
    }
    for (const resource of sorted(context.model.resources, resource => resource.name)) {
        text(resource.name, 'Resource name');
        if (allNames.has(resource.name)) {
            throw new Error(`Duplicate resource '${resource.name}'.`);
        }
        allNames.add(resource.name);
        if (environments.includes(resource) || resource.type === 'hosting/parameter') {
            continue;
        }
        const configuration = optionsFor(resource);
        if (configuration.exclude) {
            continue;
        }
        const assignment = resource.computeEnvironment === undefined
            ? resource.configuration['publish.computeEnvironment'] : resource.computeEnvironment ?? undefined;
        if (resource.computeEnvironment !== undefined && resource.configuration['publish.computeEnvironment'] !== undefined &&
            resource.configuration['publish.computeEnvironment'] !== assignment) {
            throw new Error(`Resource '${resource.name}' has conflicting compute-environment metadata; use withComputeEnvironment() to record the native relationship.`);
        }
        if (assignment !== undefined && !environments.some(environment => environment.name === assignment)) {
            throw new Error(`Resource '${resource.name}' references unknown compute environment '${assignment}'.`);
        }
        if (assignment === undefined && environments.length > 1) {
            throw new Error(`Resource '${resource.name}' must explicitly select a compute environment with withComputeEnvironment().`);
        }
        if (assignment !== undefined && assignment !== name) {
            continue;
        }
        if (resource.kind !== 'container' && resource.kind !== 'executable') {
            throw new Error(`Resource '${resource.name}' is opaque; explicitly exclude it from Docker Compose.`);
        }
        const service = serviceName(resource.name);
        if ([...names.values()].includes(service)) {
            throw new Error('Resource names collide after Compose normalization.');
        }
        resources.set(resource.name, resource);
        names.set(resource.name, service);
        options.set(resource.name, configuration);
    }
    validateDependencies(resources);
    const parameterNames = new Map<string, string>(), variableOwners = new Map<string, string>();
    for (const name of sorted(context.model.parameters, value => value)) {
        const variable = dockerComposeParameterVariable(name);
        if (variableOwners.has(variable) && variableOwners.get(variable) !== name) {
            throw new Error('Parameter names collide after environment-variable normalization.');
        }
        parameterNames.set(name, variable);
        variableOwners.set(variable, name);
    }
    const inputs: DeploymentInputs = { schemaVersion: 1, projectName, parameters: [], variables: record() };
    const usedParameters = new Set<string>(), expressionVariables = new Map<string, string>();
    const normalize = (value: PublishExpression, depth = 0): PublishExpression => {
        if (depth > 64 || value === null || typeof value !== 'object') {
            throw new Error('Invalid or excessively nested publish expression.');
        }
        switch (value.kind) {
            case 'literal':
                text(value.value, 'Expression literal', true);
                return { kind: 'literal', value: value.value };
            case 'parameter':
                if (!parameterNames.has(value.name)) {
                    throw new Error(`Expression references undeclared parameter '${value.name}'.`);
                }
                usedParameters.add(value.name);
                return { kind: 'parameter', name: value.name };
            case 'endpoint':
                endpointValue(value, resources, names);
                return { kind: 'endpoint', resource: value.resource, endpoint: value.endpoint, property: value.property };
            case 'composite':
                if (!Array.isArray(value.parts)) {
                    throw new Error('Composite expression requires an array of parts.');
                }
                return { kind: 'composite', parts: value.parts.map(part => normalize(part, depth + 1)) };
            case 'uriEncoded': return { kind: 'uriEncoded', value: normalize(value.value, depth + 1) };
            default: throw new Error('Unsupported publish expression.');
        }
    };
    const hasParameter = (value: PublishExpression): boolean => value.kind === 'parameter' ||
        (value.kind === 'composite' && value.parts.some(hasParameter)) ||
        (value.kind === 'uriEncoded' && hasParameter(value.value));
    const evaluate = (value: PublishExpression): string => {
        switch (value.kind) {
            case 'literal': return value.value;
            case 'endpoint': return endpointValue(value, resources, names);
            case 'composite': return value.parts.map(evaluate).join('');
            case 'uriEncoded': return encodeURIComponent(evaluate(value.value));
            case 'parameter': throw new Error('A deployment parameter cannot be evaluated during publish.');
        }
    };
    const placeholder = (variable: string): string => `\${${variable}:?Required deployment input ${variable}}`;
    const render = (value: PublishExpression): string => {
        switch (value.kind) {
            case 'literal': return literal(value.value);
            case 'endpoint': return literal(endpointValue(value, resources, names));
            case 'parameter': return placeholder(parameterNames.get(value.name)!);
            case 'composite': return value.parts.map(render).join('');
            case 'uriEncoded': {
                if (!hasParameter(value)) {
                    return literal(evaluate(value));
                }
                // Encoding fragments independently changes semantics for nested/composite URI expressions.
                // Store the entire tree and interpolate its fully evaluated value only at deploy time.
                const key = JSON.stringify(value);
                let variable = expressionVariables.get(key);
                if (!variable) {
                    variable = `ASPIRE_COMPOSE_EXPR_${String(expressionVariables.size + 1).padStart(4, '0')}`;
                    expressionVariables.set(key, variable);
                    inputs.variables[variable] = value;
                }
                return placeholder(variable);
            }
        }
    };
    const compose: ComposeDocument = { name: projectName, services: record() };
    const plan: DockerComposePlan = { compose, inputs, services: [] };
    const volumes = record<ComposeRecord>(), volumeOwners = new Map<string, string>();
    for (const [name, resource] of resources) {
        const configuration = options.get(name)!;
        const image = configuration.image ?? resource.image;
        const deferred = customizable.has(name);
        if ((!image || (resource.kind === 'executable' && (!configuration.image || !configuration.build))) && !deferred) {
            throw new Error(`Resource '${name}' requires a deployment image${resource.kind === 'executable' ? ' and explicit build metadata' : ''}.`);
        }
        if (image) { validateOptions({ image }); }
        if (resource.lifecycle.length > 0 && (!configuration.replaceRunLifecycle || !configuration.healthcheck) && !deferred) {
            throw new Error(`Resource '${name}' has host-only lifecycle hooks; explicitly replaceRunLifecycle with a target healthcheck or exclude it.`);
        }
        if (configuration.replaceRunLifecycle && !configuration.healthcheck && !deferred) {
            throw new Error(`Resource '${name}' must supply a replacement target healthcheck.`);
        }
        const service: ComposeService = { image: literal(image ?? ''), labels: { 'aspire.resource': literal(name) } };
        if (configuration.build) {
            service.build = buildPaths(configuration.build, context.applicationDirectory);
        }
        // An executable's host launch path/args are not a portable image command. The image's
        // Dockerfile defines its entrypoint; explicit authored arguments still override its CMD.
        if (resource.kind === 'container' && resource.entrypoint !== null) {
            text(resource.entrypoint, 'Container entrypoint');
            service.entrypoint = [literal(resource.entrypoint)];
        }
        if (resource.arguments.length > 0) {
            service.command = resource.arguments.map(value => render(normalize(value)));
        }
        const environment = record<string>();
        for (const variable of Object.keys(resource.environment).sort(compare)) {
            if (!/^[a-zA-Z_][a-zA-Z0-9_]*$/.test(variable)) {
                throw new Error(`Resource '${name}' has an invalid environment variable name.`);
            }
            environment[variable] = render(normalize(resource.environment[variable]!));
        }
        const endpointNames = new Set<string>();
        const endpoints = sorted(resource.endpoints, value => value.name);
        for (const endpoint of endpoints) {
            if (endpointNames.has(endpoint.name)) {
                throw new Error(`Resource '${name}' has duplicate endpoints.`);
            }
            endpointNames.add(endpoint.name);
            port(endpoint.targetPort, `Endpoint '${name}/${endpoint.name}' target port`);
            if (!['tcp', 'udp'].includes(endpoint.protocol) || !/^[a-z][a-z0-9+.-]*$/.test(endpoint.scheme)) {
                throw new Error(`Endpoint '${name}/${endpoint.name}' has unsupported protocol or scheme.`);
            }
            if (endpoint.environmentVariable) {
                if (!/^[a-zA-Z_][a-zA-Z0-9_]*$/.test(endpoint.environmentVariable)) {
                    throw new Error(`Endpoint '${name}/${endpoint.name}' has an invalid port environment variable.`);
                }
                const target = String(endpoint.targetPort);
                if (environment[endpoint.environmentVariable] !== undefined && environment[endpoint.environmentVariable] !== target) {
                    throw new Error(`Endpoint '${name}/${endpoint.name}' port environment variable conflicts with authored environment.`);
                }
                environment[endpoint.environmentVariable] = target;
            }
        }
        if (Object.keys(environment).length) {
            service.environment = Object.fromEntries(Object.entries(environment).sort(([a], [b]) => compare(a, b)));
        }
        if (endpoints.length) {
            service.expose = [...new Set(endpoints.map(value => `${value.targetPort}/${value.protocol}`))].sort(compare);
        }
        const exposed: DockerComposePlan['services'][number]['ports'] = [];
        // Native external-endpoint intent carries authored host-port preferences, not a
        // run allocation. An omitted/zero preference asks Compose to choose a host port.
        const portOptions: NonNullable<DockerComposeServiceOptions['ports']> = configuration.ports ??
            endpoints.filter(endpoint => endpoint.isExternal).map(endpoint => ({
                endpoint: endpoint.name, ...(endpoint.hostPort > 0 ? { published: endpoint.hostPort } : {}),
            }));
        for (const exposure of sorted(portOptions, value => value.endpoint)) {
            const endpoint = endpoints.find(value => value.name === exposure.endpoint);
            if (!endpoint) {
                throw new Error(`Exposed endpoint '${name}/${exposure.endpoint}' does not exist.`);
            }
            if (exposure.published !== undefined) { port(exposure.published, 'Published port'); }
            exposed.push({ endpoint: endpoint.name, target: endpoint.targetPort, protocol: endpoint.protocol,
                scheme: endpoint.scheme, hostIp: exposure.hostIp ?? '127.0.0.1',
                ...(exposure.published === undefined ? {} : { published: exposure.published }) });
        }
        if (exposed.length) {
            service.ports = exposed.map(value => ({ target: value.target,
                ...(value.published === undefined ? {} : { published: String(value.published) }),
                host_ip: value.hostIp, protocol: value.protocol }));
        }
        if (resource.mounts.length) {
            service.volumes = sorted(resource.mounts, value => `${value.target}\0${value.source}`).map((mount, index) => {
                if (mount.kind === 'bind') {
                    if (!mount.target.startsWith('/') || mount.target.includes('\0') || !mount.source || mount.source.includes('\0')) {
                        throw new Error(`Resource '${name}' has an invalid bind mount.`);
                    }
                    const variable = `${name.toUpperCase().replace(/[^A-Z0-9_]/g, '_')}_BINDMOUNT_${index}`;
                    if (inputs.environment?.[variable]) {
                        throw new Error('Bind mount environment-variable names collide after normalization.');
                    }
                    (inputs.environment ??= record())[variable] = {
                        description: `Bind mount source for ${name}:${mount.target}`, defaultValue: mount.source, secret: false,
                    };
                    return { type: 'bind', source: `\${${variable}:?Required deployment input ${variable}}`,
                        target: literal(mount.target), read_only: mount.readOnly };
                }
                if (mount.kind !== 'volume') {
                    throw new Error(`Resource '${name}' has an unsupported mount kind '${mount.kind}'.`);
                }
                if (!/^[a-zA-Z0-9][a-zA-Z0-9_.-]*$/.test(mount.source) || !mount.target.startsWith('/') || mount.target.includes('\0')) {
                    throw new Error(`Resource '${name}' has an invalid named volume.`);
                }
                const key = serviceName(mount.source);
                if (volumeOwners.has(key) && volumeOwners.get(key) !== mount.source) {
                    throw new Error('Volume names collide after Compose normalization.');
                }
                volumeOwners.set(key, mount.source);
                volumes[key] = {};
                return { type: 'volume', source: key, target: literal(mount.target), read_only: mount.readOnly };
            });
        }
        if (resource.dependencies.length) {
            service.depends_on = record();
            for (const dependency of [...new Set(resource.dependencies)].sort(compare)) {
                service.depends_on[names.get(dependency)!] = {
                    condition: options.get(dependency)!.healthcheck ? 'service_healthy' : 'service_started',
                };
            }
        }
        if (configuration.healthcheck) {
            service.healthcheck = { ...configuration.healthcheck, test: configuration.healthcheck.test.map(literal) };
        }
        compose.services[names.get(name)!] = service;
        plan.services.push({ resource: name, service: names.get(name)!, healthcheck: configuration.healthcheck !== undefined, ports: exposed });
    }
    if (Object.keys(volumes).length) {
        compose.volumes = Object.fromEntries(Object.entries(volumes).sort(([a], [b]) => compare(a, b)));
    }
    inputs.parameters = [...usedParameters].sort(compare).map(name => ({ name, environmentVariable: parameterNames.get(name)! }));
    if (context.model.secretParameters !== undefined || context.model.parameterDefaults !== undefined) {
        for (const parameter of inputs.parameters) {
            const secret = context.model.secretParameters?.includes(parameter.name) ?? true;
            const defaultValue = secret ? undefined : context.model.parameterDefaults?.[parameter.name];
            (inputs.environment ??= record())[parameter.environmentVariable] = {
                description: `Deployment parameter ${parameter.name}`, secret,
                ...(defaultValue === undefined ? {} : { defaultValue }),
            };
        }
    }
    if (environmentResource) {
        const source = environmentResource.configuration['publish.dockerComposeEnvironment'];
        const configuration: EnvironmentConfiguration = source === undefined
            ? { defaultNetworkName: null, dashboardEnabled: true, dashboardHostPort: null, dashboardForwardedHeaders: false }
            : environmentConfiguration(source);
        if (configuration.defaultNetworkName !== null) {
            text(configuration.defaultNetworkName, 'Default network name');
            compose.networks = { default: { name: literal(configuration.defaultNetworkName), external: true } };
        }
        if (configuration.dashboardEnabled) {
            const dashboardName = serviceName(`${name}-dashboard`);
            if (compose.services[dashboardName]) {
                throw new Error(`Dashboard service '${dashboardName}' collides with an authored workload.`);
            }
            const dashboardToken = dockerComposeParameterVariable(`${name}-dashboard-token`);
            const dashboardOtlp = dockerComposeParameterVariable(`${name}-dashboard-otlp-key`);
            if (configuration.dashboardHostPort !== null) { port(configuration.dashboardHostPort, 'Dashboard host port'); }
            const entry = (variable: string, description: string): string => {
                if (inputs.environment?.[variable] || inputs.parameters.some(parameter => parameter.environmentVariable === variable)) {
                    throw new Error(`Dashboard deployment input '${variable}' collides with a declared parameter.`);
                }
                (inputs.environment ??= record())[variable] = { description, secret: true };
                return `\${${variable}:?Required deployment input ${variable}}`;
            };
            compose.services[dashboardName] = {
                image: 'mcr.microsoft.com/dotnet/aspire-dashboard:13.0', restart: 'always',
                labels: { 'aspire.resource': `${name}-dashboard` },
                environment: {
                    ASPNETCORE_URLS: 'http://0.0.0.0:18888',
                    DOTNET_DASHBOARD_OTLP_ENDPOINT_URL: 'http://0.0.0.0:18889',
                    'Dashboard__Frontend__AuthMode': 'BrowserToken',
                    'Dashboard__Frontend__BrowserToken': entry(dashboardToken, 'Dashboard browser token'),
                    'Dashboard__Otlp__AuthMode': 'ApiKey',
                    'Dashboard__Otlp__PrimaryApiKey': entry(dashboardOtlp, 'Dashboard OTLP API key'),
                    ASPIRE_DASHBOARD_TELEMETRY_OPTOUT: 'true',
                    ASPIRE_DASHBOARD_FORWARDEDHEADERS_ENABLED: String(configuration.dashboardForwardedHeaders),
                },
                expose: ['18888/tcp', '18889/tcp'],
                ...(configuration.dashboardHostPort === null ? {} : {
                    ports: [{ target: 18888, published: String(configuration.dashboardHostPort), host_ip: '127.0.0.1', protocol: 'tcp' }],
                }),
            };
            plan.services.push({ resource: `${name}-dashboard`, service: dashboardName, healthcheck: false,
                ports: configuration.dashboardHostPort === null ? [] :
                    [{ endpoint: 'http', target: 18888, protocol: 'tcp', scheme: 'http', hostIp: '127.0.0.1',
                        published: configuration.dashboardHostPort }] });
        }
    }
    if (Object.keys(compose.services).length === 0) {
        throw new Error('Docker Compose requires at least one service.');
    }
    return plan;
}

function environmentConfiguration(source: string): EnvironmentConfiguration {
    const value: unknown = JSON.parse(source);
    object(value, 'Docker Compose environment configuration');
    keys(value, ['defaultNetworkName', 'dashboardEnabled', 'dashboardHostPort', 'dashboardForwardedHeaders'], 'Docker Compose environment configuration');
    if ((value.defaultNetworkName !== null && typeof value.defaultNetworkName !== 'string') ||
        typeof value.dashboardEnabled !== 'boolean' || typeof value.dashboardForwardedHeaders !== 'boolean' ||
        (value.dashboardHostPort !== null && typeof value.dashboardHostPort !== 'number')) {
        throw new Error('Docker Compose environment configuration has invalid properties.');
    }
    return value as unknown as EnvironmentConfiguration;
}

/** Resolves parameters solely from explicitly supplied deployment environment values, never native local secrets. */
export function resolveDockerComposeInputs(
    plan: DockerComposePlan, model: PublishModel, environment: Readonly<Record<string, string | undefined>>,
): Record<string, string> {
    const result = record<string>();
    for (const parameter of plan.inputs.parameters) {
        const secret = model.secretParameters?.includes(parameter.name) ?? true;
        const value = environment[parameter.environmentVariable] ?? (secret ? undefined :
            plan.inputs.environment?.[parameter.environmentVariable]?.defaultValue ?? model.parameterDefaults?.[parameter.name]);
        if (value === undefined || value.length === 0 || value.includes('\0')) {
            throw new Error(`Set required deployment parameter ${parameter.environmentVariable} explicitly.`);
        }
        result[parameter.environmentVariable] = value;
    }
    for (const [variable, entry] of Object.entries(plan.inputs.environment ?? {})) {
        const value = environment[variable] ?? (entry.secret ? undefined : entry.defaultValue);
        if (value === undefined || value.length === 0 || value.includes('\0')) {
            throw new Error(`Set required deployment input ${variable} explicitly.`);
        }
        result[variable] = value;
    }
    const resources = new Map(model.resources.map(resource => [resource.name, resource]));
    const names = new Map(plan.services.map(service => [service.resource, service.service]));
    const evaluate = (value: PublishExpression): string => {
        switch (value.kind) {
            case 'literal': return value.value;
            case 'parameter': return result[dockerComposeParameterVariable(value.name)]!;
            case 'endpoint': return endpointValue(value, resources, names);
            case 'composite': return value.parts.map(evaluate).join('');
            case 'uriEncoded': return encodeURIComponent(evaluate(value.value));
        }
    };
    for (const [variable, expression] of Object.entries(plan.inputs.variables)) {
        result[variable] = evaluate(expression);
    }
    return result;
}

/** Bounded, shell-free process execution. Both pipes are drained concurrently; output never reaches logs directly. */
export const runDockerCommand: DockerCommandRunner = async (executable, arguments_, options) => {
    options.signal.throwIfAborted();
    return await new Promise<DockerCommandResult>((resolveResult, reject) => {
        const child = spawn(executable, [...arguments_], { cwd: options.cwd, env: { ...options.env },
            shell: false, stdio: ['ignore', 'pipe', 'pipe'], windowsHide: true });
        const stdout: Buffer[] = [], stderr: Buffer[] = [];
        let length = 0, stopped = false, killTimer: ReturnType<typeof setTimeout> | undefined;
        const stop = (): void => {
            if (stopped) {
                return;
            }
            stopped = true;
            child.kill('SIGTERM');
            killTimer = setTimeout(() => child.kill('SIGKILL'), 2000);
            killTimer.unref();
        };
        const timer = setTimeout(stop, options.timeoutMs);
        timer.unref();
        options.signal.addEventListener('abort', stop, { once: true });
        if (options.signal.aborted) {
            stop();
        }
        const capture = (destination: Buffer[]) => (chunk: Buffer): void => {
            length += chunk.length;
            if (length > 1024 * 1024) {
                stop();
            } else {
                destination.push(chunk);
            }
        };
        child.stdout.on('data', capture(stdout));
        child.stderr.on('data', capture(stderr));
        const cleanup = (): void => {
            clearTimeout(timer);
            clearTimeout(killTimer);
            options.signal.removeEventListener('abort', stop);
        };
        child.on('error', () => {
            cleanup();
            reject(new Error('Could not start Docker. Install Docker CLI and ensure it is on PATH.'));
        });
        child.on('close', code => {
            cleanup();
            if (stopped) {
                reject(new Error('Docker command was cancelled, timed out, or exceeded the bounded output limit.'));
                return;
            }
            resolveResult({ exitCode: code ?? -1, stdout: Buffer.concat(stdout).toString('utf8'), stderr: Buffer.concat(stderr).toString('utf8') });
        });
    });
};

function redact(value: string, secrets: readonly string[]): string {
    // Docker can quote values as JSON or URI components in diagnostics. Redact each representation.
    const representations = new Set(secrets.flatMap(secret => [
        secret, encodeURIComponent(secret), JSON.stringify(secret).slice(1, -1),
    ]));
    for (const secret of [...representations].filter(Boolean).sort((a, b) => b.length - a.length)) {
        value = value.replaceAll(secret, '[REDACTED]');
    }
    return value.slice(0, 4096).replace(/[\u0000-\u0008\u000b\u000c\u000e-\u001f\u007f\u001b]/g, '');
}

function queryState(plan: DockerComposePlan, output: string): DockerComposeDeploymentState {
    let rows: unknown[];
    try {
        // Compose emits either a JSON array or one object per line:
        // [{"Service":"api","State":"running","Health":"healthy","Publishers":[
        //   {"URL":"127.0.0.1","TargetPort":8080,"PublishedPort":49152,"Protocol":"tcp"}]}]
        const source = output.trim();
        const parsed: unknown = source.startsWith('[') ? JSON.parse(source) :
            source.split(/\r?\n/).filter(Boolean).map(line => JSON.parse(line));
        if (!Array.isArray(parsed)) {
            throw new Error();
        }
        rows = parsed;
    } catch {
        throw new Error('Docker Compose returned invalid service status JSON.');
    }
    const services: DockerComposeDeploymentState['services'] = [];
    for (const expected of plan.services) {
        const matches = rows.filter(row => row !== null && typeof row === 'object' &&
            (row as Record<string, unknown>).Service === expected.service) as Record<string, unknown>[];
        if (matches.length !== 1 || matches[0]!.State !== 'running' ||
            (expected.healthcheck && matches[0]!.Health !== 'healthy') ||
            (!expected.healthcheck && ![undefined, '', 'healthy'].includes(matches[0]!.Health as string | undefined))) {
            throw new Error(`Docker Compose service '${expected.resource}' is missing, not running, or not healthy.`);
        }
        const row = matches[0]!;
        const endpoints: DockerComposeDeploymentState['services'][number]['endpoints'] = [];
        for (const exposure of expected.ports) {
            const publishers = (Array.isArray(row.Publishers) ? row.Publishers : []) as Record<string, unknown>[];
            const matching = publishers.filter(value => value !== null && typeof value === 'object' &&
                value.TargetPort === exposure.target && value.Protocol === exposure.protocol && value.URL === exposure.hostIp);
            if (matching.length !== 1 || !Number.isInteger(matching[0]!.PublishedPort) ||
                (matching[0]!.PublishedPort as number) < 1 || (matching[0]!.PublishedPort as number) > 65535 ||
                (exposure.published !== undefined && matching[0]!.PublishedPort !== exposure.published)) {
                throw new Error(`Docker Compose service '${expected.resource}' has an unexpected published binding.`);
            }
            const published = matching[0]!.PublishedPort as number;
            const host = ['0.0.0.0', '::'].includes(exposure.hostIp) ? 'localhost' :
                isIP(exposure.hostIp) === 6 ? `[${exposure.hostIp}]` : exposure.hostIp;
            endpoints.push({ name: exposure.endpoint, hostIp: exposure.hostIp, port: published, url: `${exposure.scheme}://${host}:${published}` });
        }
        services.push({ resource: expected.resource, service: expected.service, state: 'running',
            health: row.Health === 'healthy' ? 'healthy' : 'not-configured', endpoints });
    }
    return { schemaVersion: 1, projectName: plan.compose.name, services };
}

export class DockerComposeEnvironment {
    private runner: DockerCommandRunner;
    private readonly waitTimeoutSeconds: number;
    private readonly plans = new WeakMap<PublishModel, Promise<DockerComposePlan>>();
    constructor(readonly name: string, private readonly options: DockerComposeEnvironmentOptions = {},
        private readonly environmentBuilder?: DockerComposeEnvironmentBuilder) {
        text(name, 'Docker Compose environment name');
        this.runner = options.runner ?? runDockerCommand;
        this.waitTimeoutSeconds = options.waitTimeoutSeconds ?? 120;
        if (!Number.isInteger(this.waitTimeoutSeconds) || this.waitTimeoutSeconds < 1 || this.waitTimeoutSeconds > 3600) {
            throw new Error('Compose wait timeout must be between 1 and 3600 seconds.');
        }
    }

    withRunner(runner: DockerCommandRunner): this {
        this.runner = runner;
        return this;
    }

    async plan(context: PublishingContext): Promise<DockerComposePlan> {
        if (!this.environmentBuilder) {
            return generateDockerCompose(context, this.name);
        }
        let plan = this.plans.get(context.model);
        if (!plan) {
            plan = this.customize(context, this.environmentBuilder);
            this.plans.set(context.model, plan);
        }
        return await plan;
    }

    private async customize(context: PublishingContext, environment: DockerComposeEnvironmentBuilder): Promise<DockerComposePlan> {
        const customizable = new Set(environment.applicationBuilder.resources.filter(resource => serviceCallbacks.has(resource)).map(resource => resource.name));
        const plan = createDockerCompose(context, this.name, customizable);
        const originalParameters = new Set(plan.inputs.parameters.map(parameter => parameter.environmentVariable));
        // The native graph is authoritative. Only selected generated services are customized,
        // in authored order, after export; callbacks never resolve native runtime values.
        for (const entry of plan.services) {
            const builder = environment.applicationBuilder;
            const authored = builder.resources.find(resource => resource.name === entry.resource);
            if (!authored) { continue; }
            const target = new DockerComposeServiceResource(entry.resource, environment, plan, context.model);
            const service = new ComposeServiceBuilder(plan.compose.services[entry.service]!, value => {
                // Preserve only parameter placeholders captured by this callback. An arbitrary
                // "${HOME}" or "$PASSWORD" authored string must remain literal to Compose.
                const placeholders = new Set(plan.inputs.parameters.map(parameter =>
                    `\${${parameter.environmentVariable}:?Required deployment input ${parameter.environmentVariable}}`));
                return value.split(/(\$\{[^}]*\})/).map(part => placeholders.has(part) ? part : literal(part)).join('');
            }, context.applicationDirectory);
            for (const callback of serviceCallbacks.get(authored) ?? []) {
                context.signal.throwIfAborted(); await callback(target, service);
            }
        }
        const file = new ComposeFileBuilder(plan.compose, context.applicationDirectory);
        const originalProjectName = plan.compose.name;
        for (const callback of environment.resource.fileCallbacks) {
            context.signal.throwIfAborted(); await callback(file);
        }
        if (plan.compose.name !== originalProjectName &&
            context.model.resources.filter(resource => resource.type === 'docker-compose/environment').length > 1) {
            // An explicit project basename must not collapse distinct native environments into
            // one Docker project, especially when both callbacks choose the same basename.
            plan.compose.name = `${plan.compose.name}-${identity(originalProjectName)}`;
        }
        plan.inputs.projectName = plan.compose.name;
        for (const entry of plan.services) {
            const service = plan.compose.services[entry.service]!;
            const native = context.model.resources.find(resource => resource.name === entry.resource);
            if (native) {
                const options = optionsFor(native);
                if (!service.image || (native.kind === 'executable' && !service.build)) {
                    throw new Error(`Resource '${native.name}' requires a deployment image${native.kind === 'executable' ? ' and explicit build metadata' : ''}. Set service.image and service.build in publishAsDockerComposeService().`);
                }
                if ((native.lifecycle.length > 0 && !options.replaceRunLifecycle) ||
                    ((native.lifecycle.length > 0 || options.replaceRunLifecycle) && !service.healthcheck)) {
                    throw new Error(`Resource '${native.name}' has host-only lifecycle hooks; explicitly replaceRunLifecycle with a target healthcheck or exclude it.`);
                }
            }
            entry.healthcheck = service.healthcheck !== undefined;
        }
        for (const [name, service] of Object.entries(plan.compose.services)) {
            validateOptions({ image: service.image, ...(service.healthcheck ? { healthcheck: service.healthcheck } : {}) });
            if (service.depends_on) {
                for (const dependency of Object.keys(service.depends_on)) {
                    const producer = plan.compose.services[dependency];
                    if (!producer) { throw new Error(`Service '${name}' references missing Compose service '${dependency}'.`); }
                    service.depends_on[dependency] = { condition: producer.healthcheck ? 'service_healthy' : 'service_started' };
                }
            }
            if (!plan.services.some(entry => entry.service === name)) {
                plan.services.push({ resource: name, service: name, healthcheck: service.healthcheck !== undefined, ports: [] });
            }
            for (const mount of service.volumes ?? []) {
                if (mount.type === 'volume' && !plan.compose.volumes?.[mount.source]) {
                    throw new Error(`Service '${name}' references missing Compose volume '${mount.source}'. Add it with configureComposeFile().`);
                }
            }
        }
        const captured = record<CapturedEnvironmentVariable>();
        for (const parameter of plan.inputs.parameters) {
            const secret = context.model.secretParameters?.includes(parameter.name) ?? true;
            const defaultValue = secret ? undefined : context.model.parameterDefaults?.[parameter.name];
            if (plan.inputs.environment?.[parameter.environmentVariable] && !originalParameters.has(parameter.environmentVariable)) {
                throw new Error(`Parameter '${parameter.name}' collides with a captured Compose deployment input.`);
            }
            (plan.inputs.environment ??= record())[parameter.environmentVariable] ??= {
                description: `Deployment parameter ${parameter.name}`, secret,
                ...(defaultValue === undefined ? {} : { defaultValue }),
            };
        }
        for (const [variable, entry] of Object.entries(plan.inputs.environment ?? {})) {
            captured[variable] = new CapturedEnvironmentVariable(entry);
        }
        for (const callback of environment.resource.envCallbacks) {
            context.signal.throwIfAborted(); await callback(new ComposeReadOnlyDictionary(captured));
        }
        return plan;
    }

    private scopedContext(context: PublishingContext): PublishingContext {
        const environments = context.model.resources.filter(resource => resource.type === 'docker-compose/environment');
        return environments.length > 1 ? { ...context, outputPath: resolve(context.outputPath, serviceName(this.name)) } : context;
    }

    async publish(context: PublishingContext): Promise<void> {
        context = this.scopedContext(context);
        context.signal.throwIfAborted();
        const plan = await this.plan(context);
        // Validate every build input before creating any output. Paths are deliberately local,
        // rather than recursively copying an application tree containing .aspire or secrets.
        for (const service of Object.values(plan.compose.services)) {
            if (service.build) {
                try {
                    const contextPath = service.build.context.replaceAll('$$', '$');
                    const dockerfilePath = service.build.dockerfile.replaceAll('$$', '$');
                    if (!(await stat(contextPath)).isDirectory() || !(await stat(dockerfilePath)).isFile()) {
                        throw new Error();
                    }
                } catch {
                    throw new Error('Build context must be an existing directory with an existing Dockerfile.');
                }
            }
        }
        context.signal.throwIfAborted();
        await mkdir(context.outputPath, { recursive: true });
        await writeFile(resolve(context.outputPath, 'compose.yaml'), json(plan.compose), { signal: context.signal });
        await writeFile(resolve(context.outputPath, 'deployment-inputs.json'), json(plan.inputs), { signal: context.signal });
        await writeFile(resolve(context.outputPath, 'prerequisites.txt'), [
            'Docker Compose v2.20+ and a running Docker daemon are external prerequisites.',
            'compose.yaml contains JSON, which is valid YAML. Publish never executes Docker.',
            'Build contexts and Dockerfiles are absolute local paths; artifacts are not portable to another machine.',
            'Review your Dockerfile and .dockerignore: Docker builds may include local files. No context is copied by this publisher.',
            'Only explicitly exposed ports are published, on loopback and random host ports by default.',
            'Named volumes are project-scoped and retained by default; existing native-run volumes are not reused.',
            'Set required ASPIRE_PARAMETER_* values explicitly in the deploy process environment.',
            'Generated local native parameter values are never used. Inputs stay in memory, not in .env files.',
            'Resolved environment secrets remain visible to the Docker daemon/container administrators.',
            'Docker credentials, contexts, registry login and image cleanup are managed externally.',
            'Deployment does not perform automatic rollback, orphan removal, or destructive cleanup.',
            `External cleanup (argument list): ${JSON.stringify(['docker', 'compose', '-p', plan.compose.name, '-f', resolve(context.outputPath, 'compose.yaml'), 'down'])}`,
            'Do not add --volumes unless destruction of project data is intended.',
            '',
        ].join('\n'), { signal: context.signal });
    }

    async deploy(context: PublishingContext): Promise<void> {
        context = this.scopedContext(context);
        context.signal.throwIfAborted();
        const plan = await this.plan(context);
        const composePath = resolve(context.outputPath, 'compose.yaml');
        try {
            if (await readFile(composePath, 'utf8') !== json(plan.compose) ||
                await readFile(resolve(context.outputPath, 'deployment-inputs.json'), 'utf8') !== json(plan.inputs)) {
                throw new Error();
            }
        } catch {
            throw new Error('Deployment artifacts are missing or stale; publish this model before deploying.');
        }
        const values = resolveDockerComposeInputs(plan, context.model, this.options.parameterEnvironment ?? process.env);
        // A private interpolation environment prevents COMPOSE_FILE/PROJECT_NAME and ambient .env files
        // from redirecting deployment. Only Docker/tool lookup settings are inherited.
        const env = record<string | undefined>();
        for (const [key, value] of Object.entries(process.env)) {
            if (/^(PATH|Path|HOME|USERPROFILE|SystemRoot|SYSTEMROOT|TEMP|TMP|DOCKER_[A-Z0-9_]+|SSH_AUTH_SOCK|XDG_RUNTIME_DIR|HTTP_PROXY|HTTPS_PROXY|NO_PROXY)$/i.test(key)) {
                env[key] = value;
            }
        }
        Object.assign(env, values, { COMPOSE_DISABLE_ENV_FILE: '1' });
        // Docker/proxy settings may themselves contain registry credentials. They are inherited
        // only for tool connectivity and must receive the same diagnostic protection as inputs.
        const secrets = Object.values(env).filter((value): value is string => value !== undefined && value.length > 0);
        const invoke = async (args: string[], operation: string, timeoutMs = 30_000): Promise<string> => {
            context.signal.throwIfAborted();
            let result: DockerCommandResult;
            try {
                result = await this.runner('docker', args, { cwd: resolve(context.outputPath), env,
                    signal: context.signal, timeoutMs });
            } catch {
                throw new Error(`Docker ${operation} could not complete. Check Docker availability, timeout, and cancellation.`);
            }
            if (Buffer.byteLength(result.stdout) + Buffer.byteLength(result.stderr) > 1024 * 1024) {
                throw new Error(`Docker ${operation} exceeded the bounded output limit.`);
            }
            if (result.exitCode !== 0) {
                throw new Error(`Docker ${operation} failed (exit ${result.exitCode}): ${redact(`${result.stdout}\n${result.stderr}`, secrets)}`);
            }
            return result.stdout;
        };
        const version = (await invoke(['compose', 'version', '--short'], 'Compose preflight')).trim();
        const match = /^v?(\d+)\.(\d+)(?:\.\d+)?(?:[-+][a-zA-Z0-9.-]+)?$/.exec(version);
        if (!match || Number(match[1]) < 2 || (Number(match[1]) === 2 && Number(match[2]) < 20)) {
            throw new Error('Docker Compose v2.20 or newer is required for bounded --wait deployment.');
        }
        await invoke(['info', '--format', '{{json .ServerVersion}}'], 'daemon preflight');
        const prefix = ['compose', '--ansi', 'never', '--env-file', devNull, '-p', plan.compose.name, '-f', composePath];
        await invoke([...prefix, 'config', '--quiet'], 'Compose validation');
        await invoke([...prefix, 'up', '--build', '--detach', '--wait', '--wait-timeout', String(this.waitTimeoutSeconds)],
            'Compose apply', (this.waitTimeoutSeconds + 120) * 1000);
        const state = queryState(plan, await invoke([...prefix, 'ps', '--all', '--format', 'json'], 'Compose status'));
        context.signal.throwIfAborted();
        await writeFile(resolve(context.outputPath, 'deployment-state.json'), json(state), { signal: context.signal });
    }
}
