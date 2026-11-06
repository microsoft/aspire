// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

import assert from 'node:assert/strict';
import { mkdir, readFile, readdir, rm, writeFile } from 'node:fs/promises';
import { devNull } from 'node:os';
import { resolve } from 'node:path';
import { test } from 'node:test';
import {
    addDockerComposeEnvironment, ComposeFileBuilder, DockerComposeEnvironment, dockerComposeParameterVariable,
    dockerComposeProjectName, generateDockerCompose, publishAsDockerComposeService,
    resolveDockerComposeInputs, runDockerCommand,
} from './docker-compose.mjs';
import type { DockerCommandOptions, DockerCommandRunner, DockerComposeEnvironmentBuilder, DockerComposeServiceOptions } from './docker-compose.mjs';
import { ContainerResource, createBuilder } from './native-builder.mjs';
import type {
    DistributedApplicationBuilder, PublishExpression, PublishModel, PublishResource, PublishingContext, Resource, ResourceBuilder,
} from './native-builder.mjs';

const appDirectory = resolve('compose-test-application');
const value = (value: string): PublishExpression => ({ kind: 'literal', value });
const parameter = (name: string): PublishExpression => ({ kind: 'parameter', name });
const endpoint = (resource: string, property: 'Host' | 'Port' | 'HostAndPort' | 'Scheme' | 'Url' = 'Url'): PublishExpression =>
    ({ kind: 'endpoint', resource, endpoint: 'http', property });
const encode = (value: PublishExpression): PublishExpression => ({ kind: 'uriEncoded', value });
const composite = (...parts: PublishExpression[]): PublishExpression => ({ kind: 'composite', parts });
const config = (options: DockerComposeServiceOptions): Record<string, string> =>
    ({ 'publish.dockerCompose': JSON.stringify(options) });
const resource = (name: string, overrides: Partial<PublishResource> = {}): PublishResource => ({
    name, type: 'test.container', kind: 'container', image: 'example/service:1', entrypoint: null,
    executable: null, workingDirectory: null, environment: {}, arguments: [], endpoints: [], mounts: [],
    dependencies: [], lifecycle: [], configuration: {}, ...overrides,
});
const http = (overrides = {}): PublishResource['endpoints'][number] => ({
    name: 'http', protocol: 'tcp', scheme: 'http', targetPort: 8080, hostPort: 12345, environmentVariable: '', ...overrides,
});
const context = (resources: PublishResource[], parameters: string[] = []): PublishingContext => ({
    model: { schemaVersion: 1, parameters, resources }, applicationDirectory: appDirectory,
    environmentName: 'Production', outputPath: resolve('compose-test-output'), signal: new AbortController().signal,
});
const plain = <T,>(value: T): T => JSON.parse(JSON.stringify(value)) as T;
const placeholder = (name: string): string => `\${${name}:?Required deployment input ${name}}`;

test('generation covers complete container deployment shape and is deterministic without run values', () => {
    const database = resource('db', {
        image: 'postgres:17', entrypoint: '/entrypoint', arguments: [value('--log=$LEVEL')],
        environment: { PASSWORD: parameter('db-password') }, endpoints: [http({ targetPort: 5432, scheme: 'postgres' })],
        mounts: [{ kind: 'volume', source: 'db-data', target: '/var/lib/postgresql/data', readOnly: false }],
        lifecycle: ['health'],
        configuration: config({ healthcheck: { test: ['CMD-SHELL', 'echo $READY && pg_isready'], interval: '5s', timeout: '1s', retries: 4 },
            replaceRunLifecycle: true }),
    });
    const api = resource('api', {
        environment: { DATABASE: composite(value('postgres://'), parameter('db-password'), value('@'), endpoint('db', 'HostAndPort')),
            HOST: endpoint('db', 'Host'), PORT: endpoint('db', 'Port'), SCHEME: endpoint('db', 'Scheme'), URL: endpoint('db'),
            SELF_PORT: endpoint('api', 'Port'), SHELL: value('echo $HOME ${USER} $$ $(date)') },
        dependencies: ['db'], endpoints: [http({ environmentVariable: 'HTTP_PORTS' })],
        configuration: config({ ports: [{ endpoint: 'http' }] }),
    });
    const input = context([database, api], ['db-password']);
    const plan = generateDockerCompose(input, 'compose');
    const projectName = dockerComposeProjectName(appDirectory, 'Production', 'compose');
    assert.deepEqual(plain(plan), {
        compose: {
            name: projectName,
            services: {
                api: { image: 'example/service:1', labels: { 'aspire.resource': 'api' },
                    environment: { DATABASE: `postgres://${placeholder('ASPIRE_PARAMETER_DB_PASSWORD')}@db:5432`,
                        HOST: 'db', HTTP_PORTS: '8080', PORT: '5432', SCHEME: 'postgres', SELF_PORT: '8080',
                        SHELL: 'echo $$HOME $${USER} $$$$ $$(date)', URL: 'postgres://db:5432' },
                    expose: ['8080/tcp'], ports: [{ target: 8080, host_ip: '127.0.0.1', protocol: 'tcp' }],
                    depends_on: { db: { condition: 'service_healthy' } } },
                db: { image: 'postgres:17', labels: { 'aspire.resource': 'db' },
                    entrypoint: ['/entrypoint'], command: ['--log=$$LEVEL'],
                    environment: { PASSWORD: placeholder('ASPIRE_PARAMETER_DB_PASSWORD') }, expose: ['5432/tcp'],
                    volumes: [{ type: 'volume', source: 'db-data', target: '/var/lib/postgresql/data', read_only: false }],
                    healthcheck: { test: ['CMD-SHELL', 'echo $$READY && pg_isready'], interval: '5s', timeout: '1s', retries: 4 } },
            }, volumes: { 'db-data': {} },
        },
        inputs: { schemaVersion: 1, projectName, parameters: [{ name: 'db-password', environmentVariable: 'ASPIRE_PARAMETER_DB_PASSWORD' }], variables: {} },
        services: [
            { resource: 'api', service: 'api', healthcheck: false,
                ports: [{ endpoint: 'http', target: 8080, protocol: 'tcp', scheme: 'http', hostIp: '127.0.0.1' }] },
            { resource: 'db', service: 'db', healthcheck: true, ports: [] },
        ],
    });
    assert.deepEqual(generateDockerCompose(context([api, database], ['db-password']), 'compose'), plan);
    assert.equal(api.endpoints[0]!.hostPort, 12345);
    assert.deepEqual(api.environment.SHELL, value('echo $HOME ${USER} $$ $(date)'));
});

test('URI expressions are evaluated as whole trees with required inputs, preserving nested encoding and dollars', () => {
    const expression = encode(composite(value('a/'), parameter('password'), value('@'), endpoint('db', 'HostAndPort')));
    const nested = encode(composite(value('x='), expression));
    const input = context([resource('db', { endpoints: [http()] }), resource('api', {
        environment: { DIRECT: parameter('password'), FIRST: expression, SECOND: expression, NESTED: nested,
            LITERAL: encode(value('a/$HOME?#')) },
        arguments: [expression],
    })], ['unused', 'password']);
    const plan = generateDockerCompose(input, 'compose');
    assert.deepEqual(plain(plan.inputs), {
        schemaVersion: 1, projectName: plan.compose.name,
        parameters: [{ name: 'password', environmentVariable: 'ASPIRE_PARAMETER_PASSWORD' }],
        variables: { ASPIRE_COMPOSE_EXPR_0001: expression, ASPIRE_COMPOSE_EXPR_0002: nested },
    });
    assert.deepEqual(plain(plan.compose.services.api!.environment), {
        DIRECT: placeholder('ASPIRE_PARAMETER_PASSWORD'), FIRST: placeholder('ASPIRE_COMPOSE_EXPR_0001'),
        LITERAL: 'a%2F%24HOME%3F%23', NESTED: placeholder('ASPIRE_COMPOSE_EXPR_0002'),
        SECOND: placeholder('ASPIRE_COMPOSE_EXPR_0001'),
    });
    const secret = 'p/@$& :?#%é';
    const encoded = encodeURIComponent(`a/${secret}@db:8080`);
    assert.deepEqual(plain(resolveDockerComposeInputs(plan, input.model, { ASPIRE_PARAMETER_PASSWORD: secret,
        ASPIRE_COMPOSE_EXPR_0001: 'ambient override' })), {
        ASPIRE_PARAMETER_PASSWORD: secret, ASPIRE_COMPOSE_EXPR_0001: encoded,
        ASPIRE_COMPOSE_EXPR_0002: encodeURIComponent(`x=${encoded}`),
    });
    assert.throws(() => resolveDockerComposeInputs(plan, input.model, { password: secret }), /ASPIRE_PARAMETER_PASSWORD/);
    assert.throws(() => resolveDockerComposeInputs(plan, input.model, { ASPIRE_PARAMETER_PASSWORD: '' }), /explicitly/);
    assert.throws(() => resolveDockerComposeInputs(plan, input.model, { ASPIRE_PARAMETER_PASSWORD: 'bad\0value' }), /explicitly/);
    assert.throws(() => generateDockerCompose(context(input.model.resources, ['PASSWORD', 'password']), 'compose'), /collide/);
    assert.equal(dockerComposeParameterVariable('9--db.Password'), 'ASPIRE_PARAMETER_9__DB_PASSWORD');
});

test('project/service/volume identities normalize safely and isolate applications and environments', () => {
    const first = dockerComposeProjectName(appDirectory, 'Prod / 世界', 'Docker / Compose');
    assert.match(first, /^[a-z0-9][a-z0-9-]*$/);
    assert.equal(first, dockerComposeProjectName(resolve(appDirectory, '.'), 'Prod / 世界', 'Docker / Compose'));
    assert.notEqual(first, dockerComposeProjectName(resolve(appDirectory, '../other'), 'Prod / 世界', 'Docker / Compose'));
    assert.notEqual(first, dockerComposeProjectName(appDirectory, 'Staging', 'Docker / Compose'));
    assert.notEqual(first, dockerComposeProjectName(appDirectory, 'Prod / 世界', 'Other'));
    const plan = generateDockerCompose(context([resource('API.$Name', {
        mounts: [{ kind: 'volume', source: 'Data_One', target: '/data/$test', readOnly: true }],
    }), resource('api-name')]), 'compose');
    const actual = plan.services[0]!;
    assert.match(actual.service, /^api-name-[a-f0-9]{16}$/);
    assert.equal(plan.compose.services[actual.service]!.labels['aspire.resource'], 'API.$$Name');
    assert.deepEqual(plan.compose.services[actual.service]!.volumes, [
        { type: 'volume', source: 'data-one-ac65303e58139fc6', target: '/data/$$test', read_only: true },
    ]);
    assert.throws(() => dockerComposeProjectName(appDirectory, '', 'compose'), /environment/);
});

test('executables require explicit image and build, while container command, endpoint and dependencies are portable', () => {
    const app = resource('app', { kind: 'executable', image: null, executable: '/local/node', workingDirectory: '/local/app',
        arguments: [value('server.mjs')], endpoints: [http()], dependencies: ['db'],
        configuration: config({ image: 'example/app:1', build: { context: 'app', dockerfile: 'app/Dockerfile' },
            ports: [{ endpoint: 'http', published: 5000, hostIp: '0.0.0.0' }] }) });
    const plan = generateDockerCompose(context([app, resource('db')]), 'compose');
    assert.deepEqual(plain(plan.compose.services.app), {
        image: 'example/app:1', labels: { 'aspire.resource': 'app' },
        build: { context: resolve(appDirectory, 'app'), dockerfile: resolve(appDirectory, 'app/Dockerfile') },
        command: ['server.mjs'], expose: ['8080/tcp'],
        ports: [{ target: 8080, published: '5000', host_ip: '0.0.0.0', protocol: 'tcp' }],
        depends_on: { db: { condition: 'service_started' } },
    });
    for (const options of [{}, { image: 'example/app:1' }, { build: { context: '.', dockerfile: 'Dockerfile' } }]) {
        assert.throws(() => generateDockerCompose(context([{ ...app, dependencies: [], configuration: config(options) }]), 'compose'), /deployment image/);
    }
    assert.throws(() => generateDockerCompose(context([{ ...app, dependencies: [], configuration: config({
        image: 'app:1', build: { context: 'app', dockerfile: 'Dockerfile' },
    }) }]), 'compose'), /inside its explicit build context/);
});

test('native external endpoint intent publishes safe loopback bindings and records verified dynamic URLs', async () => {
    const api = resource('api', { endpoints: [http({ isExternal: true, hostPort: 0 })] });
    const input = context([api]);
    const plan = generateDockerCompose(input, 'compose');
    assert.deepEqual(plan.compose.services.api!.ports, [
        { target: 8080, host_ip: '127.0.0.1', protocol: 'tcp' },
    ]);
    assert.deepEqual(plan.services[0]!.ports, [
        { endpoint: 'http', target: 8080, protocol: 'tcp', scheme: 'http', hostIp: '127.0.0.1' },
    ]);
    const fixed = generateDockerCompose(context([{ ...api, endpoints: [http({ isExternal: true, hostPort: 4321 })] }]), 'compose');
    assert.deepEqual(fixed.compose.services.api!.ports, [
        { target: 8080, published: '4321', host_ip: '127.0.0.1', protocol: 'tcp' },
    ]);
    assert.equal(fixed.services[0]!.ports[0]!.published, 4321);
    const overridden = generateDockerCompose(context([{ ...api, configuration: config({
        ports: [{ endpoint: 'http', published: 5432, hostIp: '::1' }],
    }) }]), 'compose');
    assert.deepEqual(overridden.compose.services.api!.ports, [
        { target: 8080, published: '5432', host_ip: '::1', protocol: 'tcp' },
    ]);
    assert.equal(generateDockerCompose(context([{ ...api, configuration: config({ ports: [] }) }]), 'compose').compose.services.api!.ports, undefined);
    assert.equal(generateDockerCompose(context([resource('api', { endpoints: [http({ isExternal: false })] })]), 'compose').compose.services.api!.ports, undefined);
    assert.throws(() => generateDockerCompose(context([{ ...api, endpoints: [http({ isExternal: true, hostPort: 65536 })] }]), 'compose'), /Published port/);
    await fixture(async directory => {
        const { runner } = fakeRunner(['2.32.0', '"28.0"', '', '', status('', 'running')]);
        const target = new DockerComposeEnvironment('compose', { runner });
        const deployment = { ...input, outputPath: directory };
        await target.publish(deployment);
        await target.deploy(deployment);
        const state = JSON.parse(await readFile(resolve(directory, 'deployment-state.json'), 'utf8'));
        assert.deepEqual(state.services, [{
            resource: 'api', service: 'api', state: 'running', health: 'not-configured',
            endpoints: [{ name: 'http', hostIp: '127.0.0.1', port: 49152, url: 'http://127.0.0.1:49152' }],
        }]);
    });
});

test('unsupported model semantics and broken references fail before generation succeeds', () => {
    const failures: [PublishResource[], string[], RegExp][] = [
        [[resource('opaque', { kind: 'resource' })], [], /opaque/],
        [[resource('app', { mounts: [{ kind: 'tmpfs', source: '.', target: '/data', readOnly: true }] })], [], /unsupported mount/],
        [[resource('app', { lifecycle: ['beforeStart'] })], [], /host-only lifecycle/],
        [[resource('app', { lifecycle: ['provider'], configuration: config({ replaceRunLifecycle: true }) })], [], /host-only lifecycle/],
        [[resource('app', { configuration: config({ replaceRunLifecycle: true }) })], [], /replacement target healthcheck/],
        [[resource('app', { dependencies: ['missing'] })], [], /missing or excluded/],
        [[resource('app', { dependencies: ['db'] }), resource('db', { configuration: config({ exclude: true }) })], [], /missing or excluded/],
        [[resource('app', { dependencies: ['db'] }), resource('db', { dependencies: ['app'] })], [], /cycle/],
        [[resource('app', { environment: { URL: endpoint('missing') } })], [], /missing or excluded/],
        [[resource('app', { environment: { URL: encode(composite(parameter('password'), endpoint('excluded'))) } }),
            resource('excluded', { configuration: config({ exclude: true }) })], ['password'], /missing or excluded/],
        [[resource('app', { environment: { URL: endpoint('db') } }), resource('db')], [], /Endpoint 'http' is missing/],
        [[resource('app', { environment: { PASSWORD: parameter('undeclared') } })], [], /undeclared/],
        [[resource('app', { endpoints: [http({ targetPort: 0 })] })], [], /target port/],
        [[resource('app', { endpoints: [http({ protocol: 'http' })] })], [], /unsupported protocol/],
        [[resource('app', { endpoints: [http(), http()] })], [], /duplicate endpoints/],
        [[resource('app', { endpoints: [http({ environmentVariable: 'PORT' })], environment: { PORT: value('80') } })], [], /conflicts/],
        [[resource('app', { configuration: config({ ports: [{ endpoint: 'missing' }] }) })], [], /does not exist/],
        [[resource('app'), resource('app')], [], /Duplicate resource/],
        [[resource('excluded', { configuration: config({ exclude: true }) })], [], /at least one/],
        [[resource('app', { configuration: { 'publish.dockerCompose': '{bad json' } })], [], /invalid Docker Compose JSON/],
    ];
    for (const [resources, parameters, error] of failures) {
        assert.throws(() => generateDockerCompose(context(resources, parameters), 'compose'), error);
    }
    const excluded = resource('opaque', { kind: 'resource', configuration: config({ exclude: true }) });
    assert.deepEqual(Object.keys(generateDockerCompose(context([excluded, resource('app')]), 'compose').compose.services), ['app']);
});

test('service option validation rejects unsupported fields instead of dropping deployment intent', () => {
    const invalid: unknown[] = [
        null, [], { unknown: true }, { exclude: 'true' }, { build: { context: '.' } }, { image: '${IMAGE}' },
        { healthcheck: { test: ['NONE'] } }, { healthcheck: { test: ['CMD-SHELL', 'a', 'b'] } },
        { healthcheck: { test: ['CMD', 'true'], retries: 0 } },
        { healthcheck: { test: ['CMD', 'true'], interval: '0s' } },
        { healthcheck: { test: ['CMD', 'true'], timeout: 'soon' } },
        { ports: [{ endpoint: 'http', published: 0 }] }, { ports: [{ endpoint: 'http', hostIp: 'localhost' }] },
        { ports: [{ endpoint: 'http' }, { endpoint: 'http' }] },
        { ports: [{ endpoint: 'http', mode: 'host' }] }, { build: { context: '.', dockerfile: 'Dockerfile', args: { TOKEN: 'no' } } },
    ];
    for (const options of invalid) {
        assert.throws(() => generateDockerCompose(context([resource('app', { configuration: {
            'publish.dockerCompose': JSON.stringify(options),
        } })]), 'compose'));
    }
});

test('fluent authoring registers a named native compute resource and normal pipeline steps without runtime work', async () => {
    const builder = await createBuilder(['--operation=publish']);
    const target = builder.addDockerComposeEnvironment('compose');
    assert.equal(target.name(), 'compose');
    assert.equal(target.resource.typeId, 'docker-compose/environment');
    assert.deepEqual(builder.resources, [target.resource]);
    const input = context([environment(target), resource('api')]);
    await builder.pipeline().resolve(input);
    let graph: { steps: { name: string; resource: string | null; requiredBy: string[]; dependsOn: string[] }[] } | undefined;
    await builder.pipeline().register({
        configurePublishing: async value => { graph = JSON.parse(value) as typeof graph; },
        beginPublishing: async () => {}, claimPublishingSteps: async () => [],
        reportPublishingActivity: async () => {}, reportPublishingProgress: async () => {},
    });
    assert.deepEqual(graph!.steps.filter(step => step.resource === 'compose').map(step => ({
        name: step.name, resource: step.resource, requiredBy: step.requiredBy,
    })), [
        { name: 'validate-compose', resource: 'compose', requiredBy: ['before-start', 'publish-prereq', 'deploy-prereq'] },
        { name: 'prepare-compose', resource: 'compose', requiredBy: ['publish-compose'] },
        { name: 'publish-compose', resource: 'compose', requiredBy: ['publish'] },
        { name: 'deploy-compose', resource: 'compose', requiredBy: ['deploy'] },
    ]);
    assert.equal(graph!.steps.find(step => step.name === 'deploy-compose')!.dependsOn.includes('publish-compose'), true);
    const runBuilder = await createBuilder();
    const runEnvironment = addDockerComposeEnvironment(runBuilder, 'compose');
    assert.equal(runEnvironment.name(), 'compose');
    assert.deepEqual(runBuilder.resources, []);
    const calls: unknown[] = [];
    const resourceBuilder = { applicationBuilder: builder, withPublishConfiguration: (name: string, value: string): unknown => {
        calls.push([name, value]);
        return resourceBuilder;
    } } as unknown as ResourceBuilder<Resource>;
    const options = { ports: [{ endpoint: 'http' }] };
    assert.equal(publishAsDockerComposeService(resourceBuilder, options), resourceBuilder);
    assert.deepEqual(calls, [['dockerCompose', JSON.stringify(options)]]);
    assert.throws(() => new DockerComposeEnvironment('compose', { waitTimeoutSeconds: 0 }), /timeout/);
    assert.throws(() => new DockerComposeEnvironment('compose', { waitTimeoutSeconds: 3601 }), /timeout/);
});

const environment = (builder: DockerComposeEnvironmentBuilder): PublishResource => resource(builder.name(), {
    kind: 'resource', type: 'docker-compose/environment', image: null,
    configuration: { 'publish.dockerComposeEnvironment': JSON.stringify(builder.resource.configuration) },
});

test('typed service and file callbacks run sequentially after projection and capture symbolic parameters', async () => {
    const builder = await createBuilder(['--operation=publish']);
    const compose = builder.addDockerComposeEnvironment('compose').withDashboard({ enabled: false });
    const containerName = builder.addParameter('container-name');
    const api = builder.addContainer(new ContainerResource('api', 'nginx:alpine'));
    api.withComputeEnvironment(compose);
    const order: string[] = [];
    await compose.withProperties(async properties => {
        properties.defaultNetworkName.set('existing-network');
        assert.equal(await properties.defaultNetworkName.get(), 'existing-network');
        assert.equal(await properties.dashboardEnabled.get(), false);
        assert.equal(await properties.name(), 'compose');
    });
    api.publishAsDockerComposeService(async (target, service) => {
        order.push('first');
        service.containerName.set(await containerName.asEnvironmentPlaceholder(target));
        service.restart.set('unless-stopped');
        assert.equal(await target.parent().name(), 'compose');
        assert.equal(await target.name(), 'api');
        assert.deepEqual([await service.configs.count(), await service.secrets.count(), await service.ulimits.count()], [0, 0, 0]);
    }).publishAsDockerComposeService(async (_, service) => {
        order.push('second');
        assert.equal(service.restart.get(), 'unless-stopped');
        service.restart.set('always');
    });
    compose.configureComposeFile(async file => {
        order.push('file');
        file.name.set('custom-compose');
        assert.equal(file.name.get(), 'custom-compose');
        file.addNetwork('extra', { driver: 'bridge' });
        file.addService('sidecar', { image: 'busybox:1' });
        await file.addVolume('data', { driver: 'local', configure: async volume => { volume.labels.set('purpose', 'test'); } });
        file.addConfig('settings', { content: 'enabled=true' });
        file.addSecret('credentials', { external: true });
        const service = file.services.get('api');
        service.pullPolicy.set('always');
        assert.equal(service.pullPolicy.get(), 'always');
        service.addVolume('data', '/data', { isReadOnly: true });
    });
    const input = context([environment(compose), resource('container-name', { type: 'hosting/parameter', kind: 'resource' }), resource('api')], ['container-name']);
    input.model.secretParameters = [];
    input.model.parameterDefaults = { 'container-name': 'default-api' };
    compose.configureEnvFile(async entries => {
        const entry = entries.get('ASPIRE_PARAMETER_CONTAINER_NAME');
        entry.description.set('Container identity');
        entry.defaultValue.set('custom-api');
    });
    const target = new DockerComposeEnvironment('compose', {}, compose);
    const plan = await target.plan(input);
    assert.equal(await target.plan(input), plan);
    assert.deepEqual(order, ['first', 'second', 'file']);
    assert.equal(plan.compose.name, 'custom-compose');
    assert.equal(plan.inputs.projectName, 'custom-compose');
    assert.deepEqual(plain(plan.compose.services.api), {
        image: 'example/service:1', labels: { 'aspire.resource': 'api' },
        container_name: placeholder('ASPIRE_PARAMETER_CONTAINER_NAME'), restart: 'always', pull_policy: 'always',
        volumes: [{ type: 'volume', source: 'data', target: '/data', read_only: true }],
    });
    assert.deepEqual(plain(plan.compose.networks), { default: { name: 'existing-network', external: true }, extra: { driver: 'bridge' } });
    assert.deepEqual(plain(plan.compose.volumes), { data: { driver: 'local', labels: { purpose: 'test' } } });
    assert.deepEqual(plain(plan.compose.configs), { settings: { content: 'enabled=true' } });
    assert.deepEqual(plain(plan.compose.secrets), { credentials: { external: true } });
    assert.deepEqual(plan.services.map(service => service.service), ['api', 'sidecar']);
    assert.deepEqual(plain(resolveDockerComposeInputs(plan, input.model, {})), { ASPIRE_PARAMETER_CONTAINER_NAME: 'custom-api' });
    assert.deepEqual(plain(resolveDockerComposeInputs(plan, input.model, { ASPIRE_PARAMETER_CONTAINER_NAME: 'explicit-api' })),
        { ASPIRE_PARAMETER_CONTAINER_NAME: 'explicit-api' });
});

test('multiple native environments require explicit assignment and reject references to another environment', async () => {
    const builder = await createBuilder(['--operation=publish']);
    const first = builder.addDockerComposeEnvironment('first').withDashboard({ enabled: false });
    const second = builder.addDockerComposeEnvironment('second').withDashboard({ enabled: false });
    const assignment = (name: string): Record<string, string> => ({ 'publish.computeEnvironment': name });
    const api = resource('api', { configuration: assignment('first'), endpoints: [http()] });
    const db = resource('db', { configuration: assignment('second'), endpoints: [http()] });
    const input = context([environment(first), environment(second), api, db]);
    const firstPlan = generateDockerCompose(input, 'first'), secondPlan = generateDockerCompose(input, 'second');
    assert.deepEqual(firstPlan.services.map(service => service.service), ['api']);
    assert.deepEqual(secondPlan.services.map(service => service.service), ['db']);
    assert.notEqual(firstPlan.compose.name, secondPlan.compose.name);
    assert.throws(() => generateDockerCompose({ ...input, model: { ...input.model, resources: [...input.model.resources, resource('unassigned')] } }, 'first'), /explicitly select/);
    api.environment.DB = endpoint('db');
    assert.throws(() => generateDockerCompose(input, 'first'), /missing or excluded service 'db'/);
    api.environment = {};
    api.dependencies.push('db');
    assert.throws(() => generateDockerCompose(input, 'first'), /missing or excluded service 'db'/);
    assert.throws(() => generateDockerCompose(context([environment(first), resource('api', { configuration: assignment('missing') })]), 'first'), /unknown compute/);
    assert.throws(() => generateDockerCompose(context([environment(first), resource('api', {
        computeEnvironment: 'first', configuration: assignment('second'),
    })]), 'first'), /conflicting compute-environment/);
    await fixture(async directory => {
        const output = { ...context([environment(first), environment(second), { ...api, dependencies: [] }, db]), outputPath: directory };
        await new DockerComposeEnvironment('first').publish(output);
        await new DockerComposeEnvironment('second').publish(output);
        assert.deepEqual((await readdir(directory)).sort(), ['first', 'second']);
        assert.equal(JSON.parse(await readFile(resolve(directory, 'first/compose.yaml'), 'utf8')).name, firstPlan.compose.name);
        assert.equal(JSON.parse(await readFile(resolve(directory, 'second/compose.yaml'), 'utf8')).name, secondPlan.compose.name);
    });
    first.configureComposeFile(async file => { file.name.set('shared-basename'); });
    second.configureComposeFile(async file => { file.name.set('shared-basename'); });
    const firstCustomized = await new DockerComposeEnvironment('first', {}, first).plan({ ...input,
        model: { ...input.model, resources: input.model.resources.map(item => item.name === 'api' ? { ...item, dependencies: [] } : item) } });
    const secondCustomized = await new DockerComposeEnvironment('second', {}, second).plan(input);
    assert.notEqual(firstCustomized.compose.name, secondCustomized.compose.name);
    assert.match(firstCustomized.compose.name, /^shared-basename-[a-f0-9]{16}$/);
});

test('nonsecret parameter defaults are deploy inputs, explicit overrides win, and secret defaults are never used', () => {
    const input = context([resource('api', { environment: { REGION: parameter('region'), TOKEN: parameter('token') } })], ['region', 'token']);
    input.model.parameterDefaults = { region: 'west', token: 'must-not-be-used' };
    input.model.secretParameters = ['token'];
    const plan = generateDockerCompose(input, 'compose');
    assert.deepEqual(plain(resolveDockerComposeInputs(plan, input.model, { ASPIRE_PARAMETER_TOKEN: 'explicit' })),
        { ASPIRE_PARAMETER_REGION: 'west', ASPIRE_PARAMETER_TOKEN: 'explicit' });
    assert.deepEqual(plain(resolveDockerComposeInputs(plan, input.model, { ASPIRE_PARAMETER_REGION: 'east', ASPIRE_PARAMETER_TOKEN: 'explicit' })),
        { ASPIRE_PARAMETER_REGION: 'east', ASPIRE_PARAMETER_TOKEN: 'explicit' });
    assert.throws(() => resolveDockerComposeInputs(plan, input.model, {}), /ASPIRE_PARAMETER_TOKEN/);
    delete input.model.secretParameters;
    assert.throws(() => resolveDockerComposeInputs(plan, input.model, { ASPIRE_PARAMETER_TOKEN: 'explicit' }), /ASPIRE_PARAMETER_REGION/);
});

test('service customization without an environment fails through ordinary pipeline validation', async () => {
    const builder = await createBuilder(['--operation=publish']);
    builder.addContainer('api', 'nginx:alpine').publishAsDockerComposeService(async (_, service) => {
        service.restart.set('always');
    });
    const input = context([resource('api')]);
    const claims = [['validate-docker-compose'], []];
    const server = {
        configurePublishing: async () => {}, beginPublishing: async () => {},
        claimPublishingSteps: async () => claims.shift() ?? [],
        reportPublishingActivity: async () => {}, reportPublishingProgress: async () => {},
    };
    await builder.pipeline().resolve(input);
    await builder.pipeline().register(server);
    await assert.rejects(builder.pipeline().execute(server, input, 'publish'), /no Docker Compose environment.*addDockerComposeEnvironment/);
});

test('typed file customization escapes literal dollars and rejects ignored or plaintext-secret options', async () => {
    const model = { name: 'test', services: {} };
    const file = new ComposeFileBuilder(model);
    file.addNetwork('network', { labels: { purpose: '$TOKEN' } });
    await file.addVolume('volume', { configure: async volume => { volume.labels.set('purpose', '$TOKEN'); } });
    file.addConfig('settings', { content: '$TOKEN' });
    file.addSecret('secret', { external: true, name: '$TOKEN' });
    const service = file.addService('sidecar', { image: 'busybox', environment: { LITERAL: '$TOKEN' } });
    service.containerName.set('$TOKEN');
    service.labels.set('purpose', '$TOKEN');
    assert.deepEqual(plain(file.model), {
        name: 'test', networks: { network: { labels: { purpose: '$$TOKEN' } } },
        volumes: { volume: { labels: { purpose: '$$TOKEN' } } },
        configs: { settings: { content: '$$TOKEN' } }, secrets: { secret: { external: true, name: '$$TOKEN' } },
        services: { sidecar: { image: 'busybox', environment: { LITERAL: '$$TOKEN' },
            container_name: '$$TOKEN', labels: { purpose: '$$TOKEN' } } },
    });
    assert.throws(() => file.addSecret('plaintext', { external: true, file: 'password.txt' } as unknown as { external: true }), /unsupported options/);
    assert.throws(() => file.addService('invalid', { image: 'busybox', privileged: true } as unknown as { image: string }), /unsupported options/);
    assert.throws(() => file.addNetwork('invalid', { value: Number.NaN }));
});

test('fluent publish applies customization but performs no Docker calls or secret resolution', async () => {
    await fixture(async directory => {
        const builder = await createBuilder(['--operation=publish']);
        const compose = builder.addDockerComposeEnvironment('compose').withDashboard({ enabled: false });
        const token = builder.addParameter('token', { secret: true });
        builder.addContainer('api', 'nginx:alpine').publishAsDockerComposeService(async (resource, service) => {
            service.containerName.set(token.asEnvironmentPlaceholder(resource));
        });
        const input = { ...context([environment(compose), resource('api')], ['token']), outputPath: directory };
        input.model.secretParameters = ['token'];
        const { runner, calls } = fakeRunner([]);
        const target = new DockerComposeEnvironment('compose', { runner, parameterEnvironment: { ASPIRE_PARAMETER_TOKEN: 'not-for-artifacts' } }, compose);
        await target.publish(input);
        assert.deepEqual(calls, []);
        const files = (await readdir(directory)).sort();
        assert.deepEqual(files, ['compose.yaml', 'deployment-inputs.json', 'prerequisites.txt']);
        const published = JSON.parse(await readFile(resolve(directory, 'compose.yaml'), 'utf8'));
        assert.equal(published.services.api.container_name, placeholder('ASPIRE_PARAMETER_TOKEN'));
        assert.deepEqual(JSON.parse(await readFile(resolve(directory, 'deployment-inputs.json'), 'utf8')).environment,
            { ASPIRE_PARAMETER_TOKEN: { description: 'Deployment parameter token', secret: true } });
    });
});

test('registered publish closure writes artifacts without Docker commands or deployment inputs', async () => {
    await fixture(async directory => {
        const builder = await createBuilder(['--operation=publish']);
        const { runner, calls } = fakeRunner([]);
        const compose = builder.addDockerComposeEnvironment('compose')
            .withDashboard({ enabled: false }).withRunner(runner);
        builder.addParameter('password', { secret: true });
        builder.addContainer('api', 'nginx:alpine').withComputeEnvironment(compose)
            .publishAsDockerComposeService(async (_, service) => { service.restart.set('unless-stopped'); });
        const input = { ...context([environment(compose), resource('api', {
            environment: { PASSWORD: parameter('password') },
        })], ['password']), outputPath: directory };
        input.model.secretParameters = ['password'];
        const { server, completed } = fakePipelineServer();
        await builder.pipeline().resolve(input);
        await builder.pipeline().register(server);
        assert.deepEqual(calls, []);
        assert.deepEqual(await readdir(directory), []);
        await builder.pipeline().execute(server, input, 'publish');
        assert.deepEqual([...completed].sort(), [
            'prepare-compose', 'process-parameters', 'publish', 'publish-compose',
            'publish-prereq', 'validate-compose', 'validate-docker-compose',
        ]);
        assert.deepEqual(calls, []);
        assert.deepEqual((await readdir(directory)).sort(), ['compose.yaml', 'deployment-inputs.json', 'prerequisites.txt']);
        const published = JSON.parse(await readFile(resolve(directory, 'compose.yaml'), 'utf8'));
        assert.equal(published.services.api.environment.PASSWORD, placeholder('ASPIRE_PARAMETER_PASSWORD'));
        assert.equal(published.services.api.restart, 'unless-stopped');
    });
});

test('typed image/build/healthcheck callbacks finalize executable deployment metadata and dependency readiness', async () => {
    await fixture(async directory => {
        await mkdir(resolve(directory, 'web'));
        await writeFile(resolve(directory, 'web/Dockerfile'), 'FROM busybox\n');
        const builder = await createBuilder(['--operation=publish']);
        const compose = builder.addDockerComposeEnvironment('compose').withDashboard({ enabled: false });
        const web = builder.addExecutable('web', 'node', './web');
        web.publishAsDockerComposeService(async (_, service) => {
            assert.equal(service.image.get(), null);
            assert.equal(service.build.get(), null);
            service.image.set('example/web:1');
            service.build.set({ context: './web', dockerfile: './web/Dockerfile' });
            service.healthcheck.set({ test: ['CMD-SHELL', 'test "$READY" = true'], interval: '5s' });
        });
        const input = { ...context([environment(compose),
            resource('web', { kind: 'executable', image: null, executable: 'node', lifecycle: ['health'],
                configuration: config({ replaceRunLifecycle: true }) }),
            resource('consumer', { dependencies: ['web'] }),
        ]), applicationDirectory: directory, outputPath: resolve(directory, 'output') };
        const { runner, calls } = fakeRunner([]);
        const target = new DockerComposeEnvironment('compose', { runner }, compose);
        const plan = await target.plan(input);
        assert.deepEqual(plain(plan.compose.services.web), {
            image: 'example/web:1', labels: { 'aspire.resource': 'web' },
            build: { context: resolve(directory, 'web'), dockerfile: resolve(directory, 'web/Dockerfile') },
            healthcheck: { test: ['CMD-SHELL', 'test "$$READY" = true'], interval: '5s' },
        });
        assert.deepEqual(plain(plan.compose.services.consumer!.depends_on), { web: { condition: 'service_healthy' } });
        assert.equal(plan.services.find(service => service.resource === 'web')!.healthcheck, true);
        await target.publish(input);
        assert.deepEqual(calls, []);
        await assert.rejects(new DockerComposeEnvironment('compose', {}, compose).plan({
            ...input, model: { ...input.model, resources: input.model.resources.map(item =>
                item.name === 'web' ? { ...item, configuration: {} } : item) },
        }), /host-only lifecycle/);
        const incompleteBuilder = await createBuilder(['--operation=publish']);
        const incompleteCompose = incompleteBuilder.addDockerComposeEnvironment('compose').withDashboard({ enabled: false });
        incompleteBuilder.addExecutable('web', 'node', '.').publishAsDockerComposeService(async (_, service) => { service.image.set('example/web:1'); });
        await assert.rejects(new DockerComposeEnvironment('compose', {}, incompleteCompose).plan({
            ...input, model: { ...input.model, resources: [environment(incompleteCompose), resource('web', { kind: 'executable', image: null })] },
        }), /explicit build metadata/);
    });
});

test('bind mount env-file customization records nonsecret defaults while secret defaults are forbidden', async () => {
    const builder = await createBuilder(['--operation=publish']);
    const compose = builder.addDockerComposeEnvironment('compose').withDashboard({ enabled: false });
    compose.configureEnvFile(async entries => {
        const mount = entries.get('API_BINDMOUNT_0');
        mount.description.set('Customized bind mount source');
        mount.defaultValue.set('./data');
        assert.equal(mount.description.get(), 'Customized bind mount source');
        assert.equal(mount.defaultValue.get(), './data');
        assert.throws(() => entries.get('ASPIRE_PARAMETER_PASSWORD').defaultValue.set('do-not-write'), /Secret defaults/);
    });
    const input = context([environment(compose), resource('api', {
        mounts: [{ kind: 'bind', source: '/host/path', target: '/data', readOnly: true }],
        environment: { PASSWORD: parameter('password') },
    })], ['password']);
    input.model.secretParameters = ['password'];
    const plan = await new DockerComposeEnvironment('compose', {}, compose).plan(input);
    assert.deepEqual(plan.compose.services.api!.volumes, [
        { type: 'bind', source: placeholder('API_BINDMOUNT_0'), target: '/data', read_only: true },
    ]);
    assert.deepEqual(plain(plan.inputs.environment), {
        API_BINDMOUNT_0: { description: 'Customized bind mount source', defaultValue: './data', secret: false },
        ASPIRE_PARAMETER_PASSWORD: { description: 'Deployment parameter password', secret: true },
    });
    assert.deepEqual(plain(resolveDockerComposeInputs(plan, input.model, { ASPIRE_PARAMETER_PASSWORD: 'explicit' })),
        { ASPIRE_PARAMETER_PASSWORD: 'explicit', API_BINDMOUNT_0: './data' });
});

test('Dashboard defaults generate authenticated internal infrastructure with explicit deploy inputs', async () => {
    const builder = await createBuilder(['--operation=publish']);
    const compose = builder.addDockerComposeEnvironment('compose');
    assert.equal(compose.dashboardEnabled.get(), true);
    compose.withDashboard({ enabled: false }).withDashboard();
    await compose.configureDashboard(async dashboard => {
        assert.equal(dashboard.name(), 'compose-dashboard');
        dashboard.withHostPort({ port: 18888 }).withForwardedHeaders({ enabled: true });
        assert.deepEqual([dashboard.primaryEndpoint().url(), dashboard.primaryEndpoint().host(), dashboard.primaryEndpoint().port()],
            ['http://127.0.0.1:18888', '127.0.0.1', 18888]);
        assert.deepEqual([dashboard.otlpGrpcEndpoint().url(), dashboard.otlpGrpcEndpoint().port()], ['http://compose-dashboard:18889', 18889]);
    });
    const input = context([environment(compose), resource('api')]);
    const target = new DockerComposeEnvironment('compose', {}, compose);
    const plan = await target.plan(input);
    const dashboard = plan.compose.services['compose-dashboard']!;
    assert.equal(dashboard.image, 'mcr.microsoft.com/dotnet/aspire-dashboard:13.0');
    assert.deepEqual(plain(dashboard.environment), {
        ASPNETCORE_URLS: 'http://0.0.0.0:18888',
        DOTNET_DASHBOARD_OTLP_ENDPOINT_URL: 'http://0.0.0.0:18889',
        Dashboard__Frontend__AuthMode: 'BrowserToken',
        Dashboard__Frontend__BrowserToken: placeholder('ASPIRE_PARAMETER_COMPOSE_DASHBOARD_TOKEN'),
        Dashboard__Otlp__AuthMode: 'ApiKey',
        Dashboard__Otlp__PrimaryApiKey: placeholder('ASPIRE_PARAMETER_COMPOSE_DASHBOARD_OTLP_KEY'),
        ASPIRE_DASHBOARD_TELEMETRY_OPTOUT: 'true',
        ASPIRE_DASHBOARD_FORWARDEDHEADERS_ENABLED: 'true',
    });
    assert.throws(() => resolveDockerComposeInputs(plan, input.model, {}), /ASPIRE_PARAMETER_COMPOSE_DASHBOARD_TOKEN/);
    assert.deepEqual(plain(resolveDockerComposeInputs(plan, input.model, {
        ASPIRE_PARAMETER_COMPOSE_DASHBOARD_TOKEN: 'explicit-token', ASPIRE_PARAMETER_COMPOSE_DASHBOARD_OTLP_KEY: 'explicit-key',
    })), {
        ASPIRE_PARAMETER_COMPOSE_DASHBOARD_TOKEN: 'explicit-token', ASPIRE_PARAMETER_COMPOSE_DASHBOARD_OTLP_KEY: 'explicit-key',
    });
    const internal = generateDockerCompose(context([resource('compose', { kind: 'resource', type: 'docker-compose/environment' }), resource('api')]), 'compose');
    assert.equal(internal.compose.services['compose-dashboard']!.ports, undefined);
});

// Scratch paths remain under the caller's workspace; nothing uses the OS temporary directory.
async function fixture(action: (directory: string) => Promise<void>): Promise<void> {
    const directory = resolve(`.compose-test-${process.pid}-${crypto.randomUUID()}`);
    await mkdir(directory);
    try {
        await action(directory);
    } finally {
        await rm(directory, { recursive: true, force: true });
    }
}

type Call = { executable: string; args: readonly string[]; options: DockerCommandOptions };
function fakeRunner(results: (string | { exitCode: number; stdout: string; stderr: string } | Error)[]): { runner: DockerCommandRunner; calls: Call[] } {
    const calls: Call[] = [];
    const runner: DockerCommandRunner = async (executable, args, options) => {
        calls.push({ executable, args, options });
        const result = results.shift();
        assert.notEqual(result, undefined, 'Unexpected Docker invocation');
        if (result instanceof Error) {
            throw result;
        }
        return typeof result === 'string' ? { exitCode: 0, stdout: result, stderr: '' } : result!;
    };
    return { runner, calls };
}

function fakePipelineServer() {
    const dependencies = new Map<string, Set<string>>();
    const selected = new Set<string>(), running = new Set<string>(), completed = new Set<string>();
    const server = {
        configurePublishing: async (value: string): Promise<void> => {
            const graph = JSON.parse(value) as { steps: { name: string; dependsOn: string[]; requiredBy: string[] }[] };
            for (const step of graph.steps) { dependencies.set(step.name, new Set(step.dependsOn)); }
            for (const step of graph.steps) {
                for (const consumer of step.requiredBy) {
                    assert.equal(dependencies.has(consumer), true, `Unknown pipeline consumer '${consumer}'`);
                    dependencies.get(consumer)!.add(step.name);
                }
            }
        },
        beginPublishing: async (target: string): Promise<void> => {
            const visit = (name: string): void => {
                if (selected.has(name)) { return; }
                assert.equal(dependencies.has(name), true, `Unknown pipeline step '${name}'`);
                selected.add(name);
                for (const dependency of dependencies.get(name)!) { visit(dependency); }
            };
            visit(target);
        },
        claimPublishingSteps: async (): Promise<string[]> => {
            const ready = [...selected].filter(name => !running.has(name) && !completed.has(name) &&
                [...dependencies.get(name)!].every(dependency => completed.has(dependency)));
            for (const name of ready) { running.add(name); }
            return ready;
        },
        reportPublishingActivity: async (name: string, _status: string, complete: boolean, failed: boolean): Promise<void> => {
            if (complete) {
                assert.equal(failed, false, `Pipeline step '${name}' failed`);
                running.delete(name); completed.add(name);
            }
        },
        reportPublishingProgress: async (): Promise<void> => {},
    };
    return { server, completed };
}

const status = (health = 'healthy', state = 'running', host = '127.0.0.1'): string => JSON.stringify([
    { Service: 'api', State: state, Health: health,
        Publishers: [{ URL: host, TargetPort: 8080, PublishedPort: 49152, Protocol: 'tcp' }] },
]);
const deploymentResource = (): PublishResource => resource('api', {
    endpoints: [http()], environment: { PASSWORD: parameter('password'), URI: encode(parameter('password')) },
    configuration: config({ ports: [{ endpoint: 'http' }], healthcheck: { test: ['CMD', 'true'] } }),
});

test('publish writes only reviewable secret-free artifacts, validates all inputs before writes, and never invokes Docker', async () => {
    await fixture(async directory => {
        const input = { ...context([deploymentResource()], ['password']), outputPath: resolve(directory, 'output') };
        const { runner, calls } = fakeRunner([]);
        const target = new DockerComposeEnvironment('compose', { runner, parameterEnvironment: { ASPIRE_PARAMETER_PASSWORD: 'not-written' } });
        await target.publish(input);
        const files = (await readdir(input.outputPath)).sort();
        assert.deepEqual(files, ['compose.yaml', 'deployment-inputs.json', 'prerequisites.txt']);
        const plan = generateDockerCompose(input, 'compose');
        assert.deepEqual(JSON.parse(await readFile(resolve(input.outputPath, 'compose.yaml'), 'utf8')), plain(plan.compose));
        assert.deepEqual(JSON.parse(await readFile(resolve(input.outputPath, 'deployment-inputs.json'), 'utf8')), plain(plan.inputs));
        const artifacts = await Promise.all(files.map(file => readFile(resolve(input.outputPath, file), 'utf8')));
        assert.equal(artifacts.some(artifact => artifact.includes('not-written')), false);
        assert.deepEqual(calls, []);
        const badInput = { ...input, outputPath: resolve(directory, 'invalid'), model: context([resource('app', { dependencies: ['missing'] })]).model };
        await assert.rejects(target.publish(badInput), /missing or excluded/);
        await assert.rejects(statDirectory(badInput.outputPath), { code: 'ENOENT' });
    });
});

async function statDirectory(path: string): Promise<string[]> {
    return await readdir(path);
}

test('publish validates local build contexts without copying context, secrets, or output trees', async () => {
    await fixture(async directory => {
        const input = { ...context([resource('app', { kind: 'executable', image: null,
            configuration: config({ image: 'app:1', build: { context: 'source', dockerfile: 'source/Dockerfile' } }) })]),
            applicationDirectory: directory, outputPath: resolve(directory, 'output') };
        const target = new DockerComposeEnvironment('compose');
        await assert.rejects(target.publish(input), /existing directory/);
        assert.deepEqual(await readdir(directory), []);
        await mkdir(resolve(directory, 'source'));
        await writeFile(resolve(directory, 'source/Dockerfile'), 'FROM scratch\n');
        await writeFile(resolve(directory, 'source/private-data'), 'not copied');
        await target.publish(input);
        assert.deepEqual((await readdir(input.outputPath)).sort(), ['compose.yaml', 'deployment-inputs.json', 'prerequisites.txt']);
        const compose = JSON.parse(await readFile(resolve(input.outputPath, 'compose.yaml'), 'utf8'));
        assert.deepEqual(compose.services.app.build, {
            context: resolve(directory, 'source'), dockerfile: resolve(directory, 'source/Dockerfile'),
        });
    });
});

test('deploy preflights, validates, applies with bounded wait and queries actual status before writing nonsecret state', async () => {
    await fixture(async directory => {
        const input = { ...context([deploymentResource()], ['password']), outputPath: directory };
        const secret = 'p@ss/$?#%';
        const { runner, calls } = fakeRunner(['2.32.0', '"28.0"', '', '', status()]);
        const target = new DockerComposeEnvironment('compose', { runner, waitTimeoutSeconds: 45,
            parameterEnvironment: { ASPIRE_PARAMETER_PASSWORD: secret } });
        await target.publish(input);
        await target.deploy(input);
        const project = generateDockerCompose(input, 'compose').compose.name;
        const prefix = ['compose', '--ansi', 'never', '--env-file', devNull, '-p', project, '-f', resolve(directory, 'compose.yaml')];
        assert.deepEqual(calls.map(call => [call.executable, call.args]), [
            ['docker', ['compose', 'version', '--short']],
            ['docker', ['info', '--format', '{{json .ServerVersion}}']],
            ['docker', [...prefix, 'config', '--quiet']],
            ['docker', [...prefix, 'up', '--build', '--detach', '--wait', '--wait-timeout', '45']],
            ['docker', [...prefix, 'ps', '--all', '--format', 'json']],
        ]);
        assert.equal(calls[3]!.options.timeoutMs, 165000);
        for (const call of calls) {
            assert.equal(call.options.cwd, directory);
            assert.equal(call.options.signal, input.signal);
            assert.equal(call.options.env.ASPIRE_PARAMETER_PASSWORD, secret);
            assert.equal(call.options.env.ASPIRE_COMPOSE_EXPR_0001, encodeURIComponent(secret));
            assert.equal(call.options.env.COMPOSE_DISABLE_ENV_FILE, '1');
            assert.equal(call.options.env.COMPOSE_FILE, undefined);
        }
        assert.deepEqual(JSON.parse(await readFile(resolve(directory, 'deployment-state.json'), 'utf8')), {
            schemaVersion: 1, projectName: project,
            services: [{ resource: 'api', service: 'api', state: 'running', health: 'healthy',
                endpoints: [{ name: 'http', hostIp: '127.0.0.1', port: 49152, url: 'http://127.0.0.1:49152' }] }],
        });
        for (const file of await readdir(directory)) {
            const contents = await readFile(resolve(directory, file), 'utf8');
            assert.equal(contents.includes(secret), false);
            assert.equal(contents.includes(encodeURIComponent(secret)), false);
        }
    });
});

test('deploy requires fresh artifacts and explicit parameters before even preflighting Docker', async () => {
    await fixture(async directory => {
        const input = { ...context([deploymentResource()], ['password']), outputPath: directory };
        const { runner, calls } = fakeRunner([]);
        const target = new DockerComposeEnvironment('compose', { runner, parameterEnvironment: {} });
        await assert.rejects(target.deploy(input), /missing or stale/);
        await target.publish(input);
        await assert.rejects(target.deploy(input), /ASPIRE_PARAMETER_PASSWORD/);
        await writeFile(resolve(directory, 'compose.yaml'), '{}');
        await assert.rejects(target.deploy(input), /missing or stale/);
        assert.deepEqual(calls, []);
    });
});

test('preflight, config, apply and query failures never roll back and redact known secret representations', async () => {
    const secret = 'never-show/p@ss?$';
    const cases: { results: Parameters<typeof fakeRunner>[0]; pattern: RegExp; count: number }[] = [
        { results: [new Error(`missing docker ${secret}`)], pattern: /preflight could not complete/, count: 1 },
        { results: ['2.19.9'], pattern: /v2.20/, count: 1 },
        { results: ['garbage'], pattern: /v2.20/, count: 1 },
        { results: ['2.32.0', { exitCode: 1, stdout: secret, stderr: encodeURIComponent(secret) }], pattern: /daemon preflight failed/, count: 2 },
        { results: ['2.32.0', '', { exitCode: 2, stdout: '', stderr: secret }], pattern: /validation failed/, count: 3 },
        { results: ['2.32.0', '', '', { exitCode: 3, stdout: secret, stderr: 'apply denied' }], pattern: /apply failed/, count: 4 },
        { results: ['2.32.0', '', '', '', 'not JSON'], pattern: /invalid service status/, count: 5 },
        { results: ['2.32.0', '', '', '', '[]'], pattern: /missing, not running/, count: 5 },
        { results: ['2.32.0', '', '', '', status('unhealthy')], pattern: /not healthy/, count: 5 },
        { results: ['2.32.0', '', '', '', status('healthy', 'exited')], pattern: /not running/, count: 5 },
        { results: ['2.32.0', '', '', '', status('healthy', 'running', '0.0.0.0')], pattern: /unexpected published binding/, count: 5 },
    ];
    for (const failure of cases) {
        await fixture(async directory => {
            const input = { ...context([deploymentResource()], ['password']), outputPath: directory };
            const { runner, calls } = fakeRunner(failure.results);
            const target = new DockerComposeEnvironment('compose', { runner, parameterEnvironment: { ASPIRE_PARAMETER_PASSWORD: secret } });
            await target.publish(input);
            await assert.rejects(target.deploy(input), error => {
                assert(error instanceof Error);
                assert.match(error.message, failure.pattern);
                assert.equal(error.message.includes(secret), false);
                assert.equal(error.message.includes(encodeURIComponent(secret)), false);
                return true;
            });
            assert.equal(calls.length, failure.count);
            assert.equal(calls.some(call => call.args.includes('down')), false);
            await assert.rejects(readFile(resolve(directory, 'deployment-state.json')), { code: 'ENOENT' });
        });
    }
});

test('NDJSON status, IPv6 bindings, and services without healthchecks have nonsecret verified state', async () => {
    await fixture(async directory => {
        const api = resource('api', { endpoints: [http()], configuration: config({ ports: [{ endpoint: 'http', hostIp: '::1', published: 49152 }] }) });
        const input = { ...context([api, resource('db')]), outputPath: directory };
        const output = [
            JSON.stringify({ Service: 'api', State: 'running', Health: '', Publishers: [
                { TargetPort: 8080, PublishedPort: 49152, URL: '::1', Protocol: 'tcp' },
            ] }),
            JSON.stringify({ Service: 'db', State: 'running', Health: '' }),
        ].join('\n');
        const { runner } = fakeRunner(['v2.32.0-desktop.1', '', '', '', output]);
        const target = new DockerComposeEnvironment('compose', { runner });
        await target.publish(input);
        await target.deploy(input);
        const state = JSON.parse(await readFile(resolve(directory, 'deployment-state.json'), 'utf8'));
        assert.deepEqual(state.services, [
            { resource: 'api', service: 'api', state: 'running', health: 'not-configured',
                endpoints: [{ name: 'http', hostIp: '::1', port: 49152, url: 'http://[::1]:49152' }] },
            { resource: 'db', service: 'db', state: 'running', health: 'not-configured', endpoints: [] },
        ]);
    });
});

test('pre-cancelled publishing/deployment perform no writes or Docker calls', async () => {
    await fixture(async directory => {
        const input = { ...context([resource('app')]), outputPath: resolve(directory, 'output'), signal: AbortSignal.abort() };
        const { runner, calls } = fakeRunner([]);
        const target = new DockerComposeEnvironment('compose', { runner });
        await assert.rejects(target.publish(input));
        await assert.rejects(target.deploy(input));
        assert.deepEqual(await readdir(directory), []);
        assert.deepEqual(calls, []);
    });
});

test('the process runner uses argument lists, captures both streams, and bounds cancellation, timeout and output', async () => {
    const options: DockerCommandOptions = {
        cwd: process.cwd(), env: {}, signal: new AbortController().signal, timeoutMs: 10000,
    };
    const result = await runDockerCommand(process.execPath,
        ['-e', 'process.stdout.write(process.argv[1]); process.stderr.write("stderr");', '$HOME; echo not-a-shell'], options);
    assert.deepEqual(result, { exitCode: 0, stdout: '$HOME; echo not-a-shell', stderr: 'stderr' });
    await assert.rejects(runDockerCommand(process.execPath, ['-e', 'setInterval(() => {}, 1000)'], { ...options, timeoutMs: 25 }), /timed out/);
    await assert.rejects(runDockerCommand(process.execPath, ['-e', 'process.stdout.write("x".repeat(1100000))'], options), /bounded output/);
    const controller = new AbortController();
    const running = runDockerCommand(process.execPath, ['-e', 'setInterval(() => {}, 1000)'], { ...options, signal: controller.signal });
    controller.abort();
    await assert.rejects(running, /cancelled/);
    await assert.rejects(runDockerCommand('aspire-intentionally-missing-docker-cli', [], options), /Could not start Docker/);
});
