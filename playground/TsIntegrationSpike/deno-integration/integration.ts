import { existsSync } from 'node:fs';
import path from 'node:path';
import type {
    ContainerResource,
    DistributedApplicationBuilder,
    DockerfileBuilderCallbackContext,
    ExecutableResource,
    Resource,
} from '../.modules/aspire.js';
import {
    AspireExport,
    defineIntegration,
    type AspireTypeRef,
} from '../.modules/base.js';
import {
    defineAnnotation,
    getAnnotation,
    setAnnotation,
    type SerializedAnnotationStore,
} from './annotations.js';

const defaultDenoImage = 'denoland/deno:alpine-2.5.6';
const defaultPermissions = ['--allow-net', '--allow-env'];
const denoAppStateAnnotation = defineAnnotation<DenoAppState>('spike.deno/state');

const builderType: AspireTypeRef = {
    typeId: 'Aspire.Hosting/Aspire.Hosting.IDistributedApplicationBuilder',
    category: 'Handle',
    isInterface: true,
};

const executableType: AspireTypeRef = {
    typeId: 'Aspire.Hosting/Aspire.Hosting.ApplicationModel.ExecutableResource',
    category: 'Handle',
    isInterface: false,
};

const stringType: AspireTypeRef = {
    typeId: 'string',
    category: 'Primitive',
};

const numberType: AspireTypeRef = {
    typeId: 'number',
    category: 'Primitive',
};

const booleanType: AspireTypeRef = {
    typeId: 'boolean',
    category: 'Primitive',
};

const stringArrayType: AspireTypeRef = {
    typeId: 'array',
    category: 'Array',
    elementType: stringType,
};

interface AddDenoAppArgs
{
    builder: DistributedApplicationBuilder;
    name: string;
    appDirectory: string;
    scriptPath: string;
    args?: string[];
}

interface WithDenoArgsArgs
{
    resource: ExecutableResource;
    args: string[];
}

interface WithDenoPermissionsArgs
{
    resource: ExecutableResource;
    permissions: string[];
}

interface WithDenoTaskArgs
{
    resource: ExecutableResource;
    taskName: string;
    args?: string[];
}

interface WithDenoBuildTaskArgs
{
    resource: ExecutableResource;
    taskName: string;
    args?: string[];
}

interface WithDenoDockerfileBaseImageArgs
{
    resource: ExecutableResource;
    runtimeImage?: string;
    buildImage?: string;
}

interface PublishAsDenoDockerFileArgs
{
    resource: ExecutableResource;
    runtimeImage?: string;
    buildImage?: string;
    buildTask?: string;
    buildArgs?: string[];
    cache?: boolean;
    port?: number;
    dockerfilePath?: string;
    stage?: string;
    useExistingDockerfile?: boolean;
    user?: string;
}

interface DenoAppState
{
    appHostDirectory: string;
    appDirectory: string;
    scriptPath: string;
    args: string[];
    argsConfigured: boolean;
    permissions: string[];
    permissionsConfigured: boolean;
    runTask?: string;
    runTaskArgs: string[];
    buildTask?: string;
    buildTaskArgs: string[];
    runtimeImage: string;
    buildImage: string;
    dockerfileBaseImageConfigured: boolean;
}

interface DenoDockerfileState extends DenoAppState
{
    cache: boolean;
    port?: number;
    user?: string;
}

export const addDenoApp = AspireExport<AddDenoAppArgs, ExecutableResource>(
    {
        id: 'spike.deno/addDenoApp',
        method: 'addDenoApp',
        description: 'Adds a Deno application as an executable resource',
        projection: {
            capabilityKind: 'Method',
            targetTypeId: builderType.typeId,
            targetType: builderType,
            targetParameterName: 'builder',
            returnsBuilder: true,
            returnType: executableType,
            parameters: [
                { name: 'name', type: stringType },
                { name: 'appDirectory', type: stringType },
                { name: 'scriptPath', type: stringType },
                { name: 'args', type: stringArrayType, isOptional: true },
            ],
        },
    },
    async ({ builder, name, appDirectory, scriptPath, args = [] }) => {
        console.log(`[@spike/aspire-deno] addDenoApp('${name}') starting`);

        const appHostDirectory = await builder.appHostDirectory();
        const fullAppDirectory = path.resolve(appHostDirectory, appDirectory);
        if (!existsSync(fullAppDirectory)) {
            throw new Error(`Deno app directory '${appDirectory}' does not exist under '${appHostDirectory}'.`);
        }

        const state: DenoAppState = {
            appHostDirectory,
            appDirectory,
            scriptPath,
            args: [...args],
            argsConfigured: args.length > 0,
            permissions: [...defaultPermissions],
            permissionsConfigured: false,
            runTaskArgs: [],
            buildTaskArgs: [],
            runtimeImage: defaultDenoImage,
            buildImage: defaultDenoImage,
            dockerfileBaseImageConfigured: false,
        };

        const deno = await builder.addExecutable(name, 'deno', appDirectory, []);
        await deno.withRequiredCommand('deno', { helpLink: 'https://docs.deno.com/runtime/getting_started/installation/' });
        await deno.withOtlpExporter();
        await deno.withIconName('CodeJsRectangle');
        await deno.withEnvironment('DENO_ENV', 'development');
        await deno.withDeveloperCertificateTrust(true);
        await deno.withCertificateTrustEnvironment('DENO_CERT');
        await deno.withExecutableDebugSupport('deno', scriptPath, {
            runtimeExecutable: 'deno',
            launchMethod: 'direct',
        });
        await writeState(deno, state);
        await deno.withArgsCallback(configureDenoRunArgs);

        console.log(`[@spike/aspire-deno] addDenoApp('${name}') complete`);
        return deno;
    }
);

export const withDenoArgs = AspireExport<WithDenoArgsArgs, ExecutableResource>(
    {
        id: 'spike.deno/withDenoArgs',
        method: 'withDenoArgs',
        description: 'Replaces the command-line arguments passed to the Deno script',
        projection: {
            capabilityKind: 'Method',
            targetTypeId: executableType.typeId,
            targetType: executableType,
            targetParameterName: 'resource',
            returnsBuilder: true,
            returnType: executableType,
            parameters: [
                { name: 'args', type: stringArrayType },
            ],
        },
    },
    async ({ resource, args }) => {
        const state = await readState(resource);
        state.args = [...args];
        state.argsConfigured = true;
        await writeState(resource, state);

        return resource;
    }
);

export const withDenoPermissions = AspireExport<WithDenoPermissionsArgs, ExecutableResource>(
    {
        id: 'spike.deno/withDenoPermissions',
        method: 'withDenoPermissions',
        description: 'Configures the Deno permissions used for direct deno run execution',
        projection: {
            capabilityKind: 'Method',
            targetTypeId: executableType.typeId,
            targetType: executableType,
            targetParameterName: 'resource',
            returnsBuilder: true,
            returnType: executableType,
            parameters: [
                { name: 'permissions', type: stringArrayType },
            ],
        },
    },
    async ({ resource, permissions }) => {
        const state = await readState(resource);
        state.permissions = permissions.map(normalizePermission);
        state.permissionsConfigured = true;
        await writeState(resource, state);

        return resource;
    }
);

export const withDenoTask = AspireExport<WithDenoTaskArgs, ExecutableResource>(
    {
        id: 'spike.deno/withDenoTask',
        method: 'withDenoTask',
        description: 'Runs a Deno application by invoking a task from deno.json',
        projection: {
            capabilityKind: 'Method',
            targetTypeId: executableType.typeId,
            targetType: executableType,
            targetParameterName: 'resource',
            returnsBuilder: true,
            returnType: executableType,
            parameters: [
                { name: 'taskName', type: stringType },
                { name: 'args', type: stringArrayType, isOptional: true },
            ],
        },
    },
    async ({ resource, taskName, args = [] }) => {
        const state = await readState(resource);
        state.runTask = taskName;
        state.runTaskArgs = [...args];
        await writeState(resource, state);

        return resource;
    }
);

export const withDenoBuildTask = AspireExport<WithDenoBuildTaskArgs, ExecutableResource>(
    {
        id: 'spike.deno/withDenoBuildTask',
        method: 'withDenoBuildTask',
        description: 'Configures a deno task to run while generating the deployment Dockerfile',
        projection: {
            capabilityKind: 'Method',
            targetTypeId: executableType.typeId,
            targetType: executableType,
            targetParameterName: 'resource',
            returnsBuilder: true,
            returnType: executableType,
            parameters: [
                { name: 'taskName', type: stringType },
                { name: 'args', type: stringArrayType, isOptional: true },
            ],
        },
    },
    async ({ resource, taskName, args = [] }) => {
        const state = await readState(resource);
        state.buildTask = taskName;
        state.buildTaskArgs = [...args];
        await writeState(resource, state);

        return resource;
    }
);

export const withDenoDockerfileBaseImage = AspireExport<WithDenoDockerfileBaseImageArgs, ExecutableResource>(
    {
        id: 'spike.deno/withDenoDockerfileBaseImage',
        method: 'withDenoDockerfileBaseImage',
        description: 'Configures the Deno Docker images used for generated deployment Dockerfiles',
        projection: {
            capabilityKind: 'Method',
            targetTypeId: executableType.typeId,
            targetType: executableType,
            targetParameterName: 'resource',
            returnsBuilder: true,
            returnType: executableType,
            parameters: [
                { name: 'runtimeImage', type: stringType, isOptional: true },
                { name: 'buildImage', type: stringType, isOptional: true },
            ],
        },
    },
    async ({ resource, runtimeImage, buildImage }) => {
        const state = await readState(resource);
        state.runtimeImage = runtimeImage ?? state.runtimeImage;
        state.buildImage = buildImage ?? runtimeImage ?? state.buildImage;
        state.dockerfileBaseImageConfigured ||= runtimeImage !== undefined || buildImage !== undefined;
        await writeState(resource, state);

        return resource;
    }
);

export const publishAsDenoDockerFile = AspireExport<PublishAsDenoDockerFileArgs, ExecutableResource>(
    {
        id: 'spike.deno/publishAsDenoDockerFile',
        method: 'publishAsDenoDockerFile',
        description: 'Publishes a Deno application as a Dockerfile-backed container resource',
        projection: {
            capabilityKind: 'Method',
            targetTypeId: executableType.typeId,
            targetType: executableType,
            targetParameterName: 'resource',
            returnsBuilder: true,
            returnType: executableType,
            parameters: [
                { name: 'runtimeImage', type: stringType, isOptional: true },
                { name: 'buildImage', type: stringType, isOptional: true },
                { name: 'buildTask', type: stringType, isOptional: true },
                { name: 'buildArgs', type: stringArrayType, isOptional: true },
                { name: 'cache', type: booleanType, isOptional: true },
                { name: 'port', type: numberType, isOptional: true },
                { name: 'dockerfilePath', type: stringType, isOptional: true },
                { name: 'stage', type: stringType, isOptional: true },
                { name: 'useExistingDockerfile', type: booleanType, isOptional: true },
                { name: 'user', type: stringType, isOptional: true },
            ],
        },
    },
    async ({ resource, ...options }) => {
        const state = await readState(resource);
        const fullAppDirectory = path.resolve(state.appHostDirectory, state.appDirectory);
        const dockerfilePath = options.dockerfilePath ?? 'Dockerfile';
        const existingDockerfilePath = path.resolve(fullAppDirectory, dockerfilePath);
        const useExistingDockerfile = options.useExistingDockerfile ?? existsSync(existingDockerfilePath);

        if (useExistingDockerfile) {
            validateExistingDockerfileOptions(state, options, dockerfilePath);
        }

        const dockerfileState: DenoDockerfileState = {
            ...state,
            runtimeImage: options.runtimeImage ?? state.runtimeImage,
            buildImage: options.buildImage ?? options.runtimeImage ?? state.buildImage,
            buildTask: options.buildTask ?? state.buildTask,
            buildTaskArgs: options.buildArgs !== undefined ? [...options.buildArgs] : [...state.buildTaskArgs],
            cache: options.cache ?? true,
            port: options.port,
            user: options.user,
        };

        await resource.publishAsDockerFile(async (container: ContainerResource) => {
            await container.withEnvironment('DENO_ENV', 'production');
            await container.withCertificateTrustEnvironment('DENO_CERT');
            await writeState(container, dockerfileState);

            if (useExistingDockerfile) {
                await container.withDockerfile(state.appDirectory, {
                    dockerfilePath,
                    stage: options.stage,
                });
                return;
            }

            await container.withDockerfileBuilder(
                state.appDirectory,
                configureGeneratedDockerfile,
                { stage: options.stage ?? 'runtime' });
        });

        return resource;
    }
);

async function configureGeneratedDockerfile(
    context: DockerfileBuilderCallbackContext): Promise<void>
{
    const dockerfile = await context.builder();
    const container = await context.resource();
    const state = await readDockerfileState(container);

    await dockerfile.addContainerFilesStages(container);

    const build = await dockerfile.from(state.buildImage, { stageName: 'build' });
    await build
        .workDir('/app')
        .copy('.', '.', { chown: 'deno:deno' });

    if (state.cache) {
        await build.run(shellJoin(['deno', 'cache', ...state.permissions, state.scriptPath]));
    }

    if (state.buildTask) {
        await build.run(shellJoin(['deno', 'task', state.buildTask, ...state.buildTaskArgs]));
    }

    const runtime = await dockerfile.from(state.runtimeImage, { stageName: 'runtime' });
    await runtime
        .workDir('/app')
        .copyFrom('build', '/app', '/app', { chown: 'deno:deno' })
        .env('DENO_ENV', 'production');

    if (state.port !== undefined) {
        await runtime.expose(state.port);
    }

    await runtime
        .user(state.user ?? 'deno')
        .entrypoint(['deno', ...getRunArgs(state)])
        .addContainerFiles(container, '/app');
}

async function configureDenoRunArgs(context: { args(): PromiseLike<{ clear(): PromiseLike<unknown>; add(value: string): PromiseLike<unknown> }>; resource(): PromiseLike<Resource> }): Promise<void>
{
    const resource = await context.resource();
    const state = await readState(resource);
    const args = await context.args();

    await args.clear();

    for (const arg of getRunArgs(state)) {
        await args.add(arg);
    }
}

function getRunArgs(state: DenoAppState): string[]
{
    if (state.runTask) {
        return ['task', state.runTask, ...state.runTaskArgs];
    }

    return ['run', ...state.permissions, state.scriptPath, ...state.args];
}

async function readState(store: SerializedAnnotationStore): Promise<DenoAppState>
{
    const state = await getAnnotation(store, denoAppStateAnnotation);

    state.args ??= [];
    state.argsConfigured ??= state.args.length > 0;
    state.permissions ??= [...defaultPermissions];
    state.permissionsConfigured ??= false;
    state.runTaskArgs ??= [];
    state.buildTaskArgs ??= [];
    state.runtimeImage ??= defaultDenoImage;
    state.buildImage ??= state.runtimeImage;
    state.dockerfileBaseImageConfigured ??= false;

    return state;
}

async function readDockerfileState(store: SerializedAnnotationStore): Promise<DenoDockerfileState>
{
    const state = await readState(store) as DenoDockerfileState;
    state.cache ??= true;

    return state;
}

async function writeState(store: SerializedAnnotationStore, state: DenoAppState): Promise<void>
{
    await setAnnotation(store, denoAppStateAnnotation, state);
}

function validateExistingDockerfileOptions(
    state: DenoAppState,
    options: Omit<PublishAsDenoDockerFileArgs, 'resource'>,
    dockerfilePath: string): void
{
    const ignoredOptions: string[] = [];

    if (state.argsConfigured) {
        ignoredOptions.push('Deno script arguments');
    }

    if (state.permissionsConfigured) {
        ignoredOptions.push('Deno permissions');
    }

    if (state.runTask) {
        ignoredOptions.push(`run task '${state.runTask}'`);
    }

    if (state.buildTask || options.buildTask !== undefined || options.buildArgs !== undefined) {
        ignoredOptions.push('Deno build task');
    }

    if (state.dockerfileBaseImageConfigured || options.runtimeImage !== undefined || options.buildImage !== undefined) {
        ignoredOptions.push('Deno Dockerfile base images');
    }

    if (options.cache !== undefined) {
        ignoredOptions.push('Deno cache layer');
    }

    if (options.port !== undefined) {
        ignoredOptions.push('Dockerfile exposed port');
    }

    if (options.user !== undefined) {
        ignoredOptions.push('Dockerfile runtime user');
    }

    if (ignoredOptions.length === 0) {
        return;
    }

    throw new Error(
        `Deno app '${state.appDirectory}' is publishing with existing Dockerfile '${dockerfilePath}', ` +
        `so Aspire cannot apply ${ignoredOptions.join(', ')}. Remove or rename the Dockerfile so Aspire can generate one, ` +
        'or configure the Dockerfile directly.');
}

function normalizePermission(permission: string): string
{
    const value = permission.trim();
    if (value.length === 0) {
        throw new Error('Deno permission values cannot be empty.');
    }

    if (value.startsWith('--')) {
        return value;
    }

    return value.startsWith('allow-') ? `--${value}` : `--allow-${value}`;
}

function shellJoin(args: readonly string[]): string
{
    return args.map(quotePosixShellArg).join(' ');
}

function quotePosixShellArg(value: string): string
{
    if (/^[A-Za-z0-9_./:=@+-]+$/.test(value)) {
        return value;
    }

    // Dockerfile RUN commands execute under the image's POSIX shell. Single-quote
    // each argument and escape embedded single quotes to avoid script/task names or
    // paths being interpreted as shell syntax.
    return `'${value.replaceAll("'", "'\\''")}'`;
}

export default defineIntegration({
    name: 'DenoIntegration',
    capabilities: [
        addDenoApp,
        withDenoArgs,
        withDenoPermissions,
        withDenoTask,
        withDenoBuildTask,
        withDenoDockerfileBaseImage,
        publishAsDenoDockerFile,
    ],
});
