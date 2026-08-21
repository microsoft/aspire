import * as assert from 'assert';
import * as fs from 'fs';
import * as path from 'path';
import * as sinon from 'sinon';
import * as vscode from 'vscode';

import type {
    ResourceDebugger,
    ResourceDebugRequest,
    ResourceDebugResult,
} from '../debugger/resourceDebugContracts';
import {
    AppHostLifecycleToolService,
    aspireAppHostStartToolName,
    aspireAppHostStopToolName,
} from '../lm/appHostLifecycleTools';
import {
    AspireResourceDebugLanguageModelTool,
    AspireResourceDebugToolService,
    aspireResourceDebugToolName,
    registerAspireResourceDebugTool,
    type AspireResourceDebugToolInput,
    type AspireResourceDebugToolResult,
    type SafeAppHostTargetResolver,
    type SafeAppHostTargetResolution,
} from '../lm/resourceDebugTools';
import { AppHostTargetResolverService } from '../lm/appHostTargetResolverService';

const absoluteAppHostPath = '/private/workspace/AppHost/AppHost.csproj';
const safeAppHostPath = 'AppHost/AppHost.csproj';

class FakeTargetResolver implements SafeAppHostTargetResolver {
    calls = 0;
    results: SafeAppHostTargetResolution[] = [{
        resolved: true,
        target: {
            absolutePath: absoluteAppHostPath,
            relativePath: safeAppHostPath,
            displayPath: safeAppHostPath,
        },
    }];
    error: Error | undefined;
    errors: Array<Error | undefined> = [];
    tokens: vscode.CancellationToken[] = [];
    onResolve: ((token: vscode.CancellationToken) => void | Promise<void>) | undefined;

    async resolveTarget(_rawAppHost: unknown, token: vscode.CancellationToken): Promise<SafeAppHostTargetResolution> {
        this.calls++;
        this.tokens.push(token);
        await this.onResolve?.(token);
        if (token.isCancellationRequested) {
            return { resolved: false, outcome: 'cancelled' };
        }

        const error = this.errors[this.calls - 1] ?? this.error;
        if (error) {
            throw error;
        }

        return this.results[Math.min(this.calls - 1, this.results.length - 1)];
    }
}

class FakeResourceDebugger implements ResourceDebugger {
    calls: ResourceDebugRequest[] = [];
    result: ResourceDebugResult = { outcome: 'started', providerId: 'dotnet' };
    error: Error | undefined;
    onDebug: ((request: ResourceDebugRequest) => void | Promise<void>) | undefined;

    async debug(request: ResourceDebugRequest): Promise<ResourceDebugResult> {
        this.calls.push(request);
        await this.onDebug?.(request);
        if (request.cancellationToken?.isCancellationRequested) {
            return { outcome: 'cancelled' };
        }

        if (this.error) {
            throw this.error;
        }

        return this.result;
    }

    canAttachToResource(): boolean {
        return true;
    }
}

function readToolResultPayload(result: vscode.LanguageModelToolResult): AspireResourceDebugToolResult {
    const parts = result.content as Array<{ value?: unknown }>;
    assert.strictEqual(parts.length, 1, 'Tool results must be a single bounded content part.');
    assert.strictEqual(typeof parts[0]?.value, 'string');
    return JSON.parse(parts[0].value as string) as AspireResourceDebugToolResult;
}

function createService(
    targetResolver = new FakeTargetResolver(),
    resourceDebugger = new FakeResourceDebugger(),
): {
    readonly service: AspireResourceDebugToolService;
    readonly targetResolver: FakeTargetResolver;
    readonly resourceDebugger: FakeResourceDebugger;
} {
    return {
        service: new AspireResourceDebugToolService({ targetResolver, resourceDebugger }),
        targetResolver,
        resourceDebugger,
    };
}

function createInput(overrides: Record<string, unknown> = {}): Record<string, unknown> {
    return {
        appHostPath: safeAppHostPath,
        resourceName: 'api',
        ...overrides,
    };
}

suite('Aspire resource debug language model tool', () => {
    let isTrustedStub: sinon.SinonStub;

    setup(() => {
        isTrustedStub = sinon.stub(vscode.workspace, 'isTrusted').value(true);
    });

    teardown(() => {
        isTrustedStub.restore();
        sinon.restore();
    });

    suite('manifest and localization', () => {
        test('contributes the localized resource debug tool contract without changing lifecycle tools', () => {
            const extensionRoot = path.resolve(__dirname, '..', '..');
            const manifest = JSON.parse(fs.readFileSync(path.join(extensionRoot, 'package.json'), 'utf8')) as {
                activationEvents?: string[];
                contributes: { languageModelTools?: Array<Record<string, unknown>> };
            };
            const packageNls = JSON.parse(fs.readFileSync(path.join(extensionRoot, 'package.nls.json'), 'utf8')) as Record<string, string>;
            const tools = manifest.contributes.languageModelTools ?? [];
            const tool = tools.find(candidate => candidate.name === aspireResourceDebugToolName);

            assert.ok(tool);
            assert.strictEqual(tool.toolReferenceName, 'aspireDebugResource');
            assert.strictEqual(tool.icon, '$(debug-alt)');
            assert.strictEqual(tool.canBeReferencedInPrompt, true);
            assert.strictEqual(tool.when, 'isWorkspaceTrusted');
            assert.deepStrictEqual(tool.tags, ['aspire', 'debug', 'resource']);
            assert.ok(manifest.activationEvents?.includes(`onLanguageModelTool:${aspireResourceDebugToolName}`));

            for (const field of ['displayName', 'modelDescription', 'userDescription']) {
                const reference = tool[field] as string;
                assert.match(reference, /^%[\w.-]+%$/);
                assert.ok(packageNls[reference.slice(1, -1)]);
            }

            assert.deepStrictEqual(tool.inputSchema, {
                type: 'object',
                properties: {
                    appHostPath: {
                        type: 'string',
                        description: '%languageModelTool.aspireResourceDebug.appHostPath.description%',
                    },
                    resourceName: {
                        type: 'string',
                        description: '%languageModelTool.aspireResourceDebug.resourceName.description%',
                    },
                    strategy: {
                        type: 'string',
                        enum: ['auto', 'attach'],
                        default: 'auto',
                        description: '%languageModelTool.aspireResourceDebug.strategy.description%',
                    },
                },
                required: ['appHostPath', 'resourceName'],
                additionalProperties: false,
            });
            assert.deepStrictEqual(
                tools
                    .filter(candidate => candidate.name === aspireAppHostStartToolName || candidate.name === aspireAppHostStopToolName)
                    .map(candidate => [candidate.name, candidate.toolReferenceName]),
                [
                    [aspireAppHostStartToolName, 'aspireStartAppHost'],
                    [aspireAppHostStopToolName, 'aspireStopAppHost'],
                ]);
        });

        test('adds localized manifest and runtime strings for the confirmation', () => {
            const extensionRoot = path.resolve(__dirname, '..', '..');
            const packageNls = JSON.parse(fs.readFileSync(path.join(extensionRoot, 'package.nls.json'), 'utf8')) as Record<string, string>;

            assert.deepStrictEqual(
                {
                    title: packageNls['aspire-vscode.strings.resourceDebugToolConfirmationTitle'],
                    message: packageNls['aspire-vscode.strings.resourceDebugToolConfirmationMessage'],
                    invocation: packageNls['aspire-vscode.strings.resourceDebugToolInvocationMessage'],
                    unresolvedInvocation: packageNls['aspire-vscode.strings.resourceDebugToolUnavailableInvocationMessage'],
                    display: packageNls['languageModelTool.aspireResourceDebug.displayName'],
                    model: packageNls['languageModelTool.aspireResourceDebug.modelDescription'],
                    user: packageNls['languageModelTool.aspireResourceDebug.userDescription'],
                    appHostPath: packageNls['languageModelTool.aspireResourceDebug.appHostPath.description'],
                    resourceName: packageNls['languageModelTool.aspireResourceDebug.resourceName.description'],
                    strategy: packageNls['languageModelTool.aspireResourceDebug.strategy.description'],
                },
                {
                    title: 'Attach debugger to Aspire resource',
                    message: 'Attach the debugger to resource {0} from Aspire AppHost {1}?',
                    invocation: 'Attaching debugger to Aspire resource {0}...',
                    unresolvedInvocation: 'Attaching debugger to the requested Aspire resource...',
                    display: 'Debug Aspire resource',
                    model: 'Attach the VS Code debugger to a running Aspire resource that the extension has already discovered. Requires a workspace-relative AppHost path and the resource name. The default auto strategy currently attaches to the resource; start and restart under debug are not supported.',
                    user: 'Attach the debugger to a running Aspire resource.',
                    appHostPath: 'Workspace-relative path of an AppHost that Aspire has already discovered. Absolute paths and paths Aspire did not discover are rejected. In a multi-root workspace, prefix the path with the workspace folder name.',
                    resourceName: 'Name of a running resource from the selected AppHost. Resource names are limited to 256 characters.',
                    strategy: 'Debug strategy. auto selects the available safe action, currently attach. attach only attaches a debugger; starting and restarting resources are not supported.',
                });
        });
    });

    suite('registration', () => {
        test('registers and disposes the resource debug tool once', () => {
            const { service } = createService();
            const disposed: string[] = [];
            const registerToolStub = sinon.stub(vscode.lm, 'registerTool').callsFake((name: string) =>
                new vscode.Disposable(() => disposed.push(name)));

            const registration = registerAspireResourceDebugTool(service);

            assert.strictEqual(registration.registered, true);
            assert.deepStrictEqual(registerToolStub.getCalls().map(call => call.args[0]), [aspireResourceDebugToolName]);
            assert.deepStrictEqual([...registration.tools.keys()], [aspireResourceDebugToolName]);
            registration.dispose();
            assert.deepStrictEqual(disposed, [aspireResourceDebugToolName]);
        });

        test('does not register when the language model tool API is unavailable', () => {
            const { service } = createService();
            const registerToolStub = sinon.stub(vscode.lm, 'registerTool').value(undefined);

            const registration = registerAspireResourceDebugTool(service);

            assert.strictEqual(registration.registered, false);
            registration.dispose();
            registerToolStub.restore();
        });
    });

    suite('input and target resolution', () => {
        test('rejects invalid input and additional properties before resolving or debugging', async () => {
            const throwingInput = { resourceName: 'api' };
            Object.defineProperty(throwingInput, 'appHostPath', {
                enumerable: true,
                get: () => {
                    throw new Error('token=super-secret');
                },
            });
            const hiddenAdditionalPropertyInput = createInput();
            Object.defineProperty(hiddenAdditionalPropertyInput, 'hidden', {
                value: 'unexpected',
            });
            const invalidInputs: unknown[] = [
                undefined,
                null,
                [],
                { appHostPath: safeAppHostPath },
                { resourceName: 'api' },
                createInput({ appHostPath: '   ' }),
                createInput({ appHostPath: 'AppHost/\u200bAppHost.csproj' }),
                createInput({ appHostPath: 'AppHost\u2028/AppHost.csproj' }),
                createInput({ appHostPath: 'AppHost\u2029/AppHost.csproj' }),
                createInput({ resourceName: '\t' }),
                createInput({ resourceName: 'api\u200b' }),
                createInput({ resourceName: 'api\u2028injected' }),
                createInput({ resourceName: 'api\u2029injected' }),
                createInput({ resourceName: 'a'.repeat(257) }),
                createInput({ strategy: 'restart' }),
                createInput({ unexpected: 'value' }),
                throwingInput,
                hiddenAdditionalPropertyInput,
            ];

            for (const input of invalidInputs) {
                const { service, targetResolver, resourceDebugger } = createService();
                const result = await service.debug(input, new vscode.CancellationTokenSource().token);

                assert.deepStrictEqual(result, {
                    tool: aspireResourceDebugToolName,
                    success: false,
                    outcome: 'invalidInput',
                    appHost: '',
                    resourceName: '',
                    requestedStrategy: 'auto',
                    effectiveStrategy: 'none',
                    controller: 'none',
                });
                assert.strictEqual(targetResolver.calls, 0);
                assert.strictEqual(resourceDebugger.calls.length, 0);
            }
        });

        test('defaults and explicitly maps auto and attach to attach', async () => {
            for (const [input, requestedStrategy] of [
                [createInput(), 'auto'],
                [createInput({ strategy: 'auto' }), 'auto'],
                [createInput({ strategy: 'attach' }), 'attach'],
            ] as const) {
                const { service, resourceDebugger } = createService();
                const result = await service.debug(input, new vscode.CancellationTokenSource().token);

                assert.deepStrictEqual(
                    {
                        success: result.success,
                        requestedStrategy: result.requestedStrategy,
                        effectiveStrategy: result.effectiveStrategy,
                        controller: result.controller,
                    },
                    {
                        success: true,
                        requestedStrategy,
                        effectiveStrategy: 'attach',
                        controller: 'editor',
                    });
                assert.strictEqual(resourceDebugger.calls[0].source, 'languageModelTool');
                    assert.strictEqual(resourceDebugger.calls[0].strategy, requestedStrategy);
            }
        });

        test('rejects untrusted workspaces without resolving or debugging', async () => {
            isTrustedStub.value(false);
            const { service, targetResolver, resourceDebugger } = createService();

            const result = await service.debug(createInput(), new vscode.CancellationTokenSource().token);

            assert.strictEqual(result.outcome, 'workspaceNotTrusted');
            assert.strictEqual(targetResolver.calls, 0);
            assert.strictEqual(resourceDebugger.calls.length, 0);
        });

        test('maps missing, ambiguous, and failed AppHost resolution without leaking a target', async () => {
            for (const outcome of ['unknownAppHost', 'ambiguousAppHost', 'discoveryFailed'] as const) {
                const resolver = new FakeTargetResolver();
                resolver.results = [{ resolved: false, outcome }];
                const { service, resourceDebugger } = createService(resolver);

                const result = await service.debug(createInput(), new vscode.CancellationTokenSource().token);

                assert.deepStrictEqual(
                    {
                        outcome: result.outcome,
                        appHost: result.appHost,
                        controller: result.controller,
                        effectiveStrategy: result.effectiveStrategy,
                    },
                    {
                        outcome,
                        appHost: '',
                        controller: 'none',
                        effectiveStrategy: 'none',
                    });
                assert.strictEqual(resourceDebugger.calls.length, 0);
            }
        });

        test('retains the resolver safe multi-root display path and never returns its absolute target', async () => {
            const resolver = new FakeTargetResolver();
            resolver.results = [{
                resolved: true,
                target: {
                    absolutePath: '/private/workspace/backend/AppHost/AppHost.csproj',
                    relativePath: 'AppHost/AppHost.csproj',
                    displayPath: 'backend/AppHost/AppHost.csproj',
                },
            }];
            const { service, resourceDebugger } = createService(resolver);

            const result = await service.debug(
                createInput({ appHostPath: 'backend/AppHost/AppHost.csproj' }),
                new vscode.CancellationTokenSource().token);

            assert.strictEqual(result.appHost, 'backend/AppHost/AppHost.csproj');
            assert.strictEqual(resourceDebugger.calls[0].appHost.absolutePath, '/private/workspace/backend/AppHost/AppHost.csproj');
            assert.strictEqual(JSON.stringify(result).includes('/private/workspace'), false);
        });
    });

    suite('confirmation and invocation', () => {
        test('confirms only the user resource name and safe AppHost display path', async () => {
            const resolver = new FakeTargetResolver();
            resolver.results = [{
                resolved: true,
                target: {
                    absolutePath: absoluteAppHostPath,
                    relativePath: safeAppHostPath,
                    displayPath: 'backend/AppHost/AppHost.csproj',
                },
            }];
            const { service } = createService(resolver);
            const tool = new AspireResourceDebugLanguageModelTool(service);

            const prepared = await tool.prepareInvocation(
                { input: createInput({ appHostPath: 'backend/AppHost/AppHost.csproj', resourceName: 'api' }) as unknown as AspireResourceDebugToolInput },
                new vscode.CancellationTokenSource().token);
            const confirmation = `${prepared.confirmationMessages?.title}\n${prepared.confirmationMessages?.message}\n${prepared.invocationMessage}`;

            assert.strictEqual(prepared.confirmationMessages?.title, 'Attach debugger to Aspire resource');
            assert.strictEqual(prepared.confirmationMessages?.message, 'Attach the debugger to resource api from Aspire AppHost backend/AppHost/AppHost.csproj?');
            assert.strictEqual(prepared.invocationMessage, 'Attaching debugger to Aspire resource api...');
            assert.strictEqual(confirmation.includes(absoluteAppHostPath), false);
            assert.strictEqual(confirmation.includes('pid'), false);
            assert.strictEqual(confirmation.includes('debug configuration'), false);
        });

        test('always requires a generic confirmation when preparation cannot resolve the AppHost', async () => {
            const resolver = new FakeTargetResolver();
            for (const outcome of ['unknownAppHost', 'discoveryFailed', 'cancelled'] as const) {
                resolver.calls = 0;
                resolver.results = [
                    { resolved: false, outcome },
                    {
                        resolved: true,
                        target: {
                            absolutePath: absoluteAppHostPath,
                            relativePath: safeAppHostPath,
                            displayPath: safeAppHostPath,
                        },
                    },
                ];
                const { service, resourceDebugger } = createService(resolver);
                const tool = new AspireResourceDebugLanguageModelTool(service);
                const input = createInput({ appHostPath: '../private/token=secret' });

                const prepared = await tool.prepareInvocation(
                    { input: input as unknown as AspireResourceDebugToolInput },
                    new vscode.CancellationTokenSource().token);
                const result = readToolResultPayload(await tool.invoke(
                    { input: input as unknown as AspireResourceDebugToolInput, toolInvocationToken: undefined },
                    new vscode.CancellationTokenSource().token));

                assert.deepStrictEqual(prepared.confirmationMessages, {
                    title: 'Attach debugger to Aspire resource',
                    message: 'Attach the debugger to the requested Aspire resource?',
                });
                assert.strictEqual(prepared.invocationMessage, 'Attaching debugger to the requested Aspire resource...');
                assert.strictEqual(JSON.stringify(prepared).includes('../private/token=secret'), false);
                assert.strictEqual(JSON.stringify(prepared).includes(absoluteAppHostPath), false);
                assert.strictEqual(result.outcome, 'started');
                assert.strictEqual(resourceDebugger.calls.length, 1);
            }

            const { service } = createService();
            const tool = new AspireResourceDebugLanguageModelTool(service);
            const prepared = await tool.prepareInvocation(
                { input: createInput({ resourceName: 'api\u2028injected' }) as unknown as AspireResourceDebugToolInput },
                new vscode.CancellationTokenSource().token);

            assert.deepStrictEqual(prepared.confirmationMessages, {
                title: 'Attach debugger to Aspire resource',
                message: 'Attach the debugger to the requested Aspire resource?',
            });
            assert.strictEqual(JSON.stringify(prepared).includes('injected'), false);
        });

        test('allows an invocation to resolve after preparation fails', async () => {
            const resolver = new FakeTargetResolver();
            resolver.errors = [new Error('initial AppHost discovery failure'), undefined];
            const { service, resourceDebugger } = createService(resolver);
            const tool = new AspireResourceDebugLanguageModelTool(service);
            const input = createInput();

            const prepared = await tool.prepareInvocation(
                { input: input as unknown as AspireResourceDebugToolInput },
                new vscode.CancellationTokenSource().token);
            const result = readToolResultPayload(await tool.invoke(
                { input: input as unknown as AspireResourceDebugToolInput, toolInvocationToken: undefined },
                new vscode.CancellationTokenSource().token));

            assert.deepStrictEqual(prepared.confirmationMessages, {
                title: 'Attach debugger to Aspire resource',
                message: 'Attach the debugger to the requested Aspire resource?',
            });
            assert.strictEqual(prepared.invocationMessage, 'Attaching debugger to the requested Aspire resource...');
            assert.strictEqual(result.outcome, 'started');
            assert.strictEqual(resourceDebugger.calls.length, 1);
        });

        test('escapes confirmed resource and AppHost identities with the shared Markdown helper', async () => {
            const resolver = new FakeTargetResolver();
            resolver.results = [{
                resolved: true,
                target: {
                    absolutePath: absoluteAppHostPath,
                    relativePath: 'AppHost/[unsafe]*.csproj',
                    displayPath: 'AppHost/[unsafe]*.csproj',
                },
            }];
            const { service } = createService(resolver);
            const tool = new AspireResourceDebugLanguageModelTool(service);

            const prepared = await tool.prepareInvocation(
                { input: createInput({ resourceName: 'api_[unsafe]*' }) as unknown as AspireResourceDebugToolInput },
                new vscode.CancellationTokenSource().token);

            assert.strictEqual(
                prepared.confirmationMessages?.message,
                'Attach the debugger to resource api\\_\\[unsafe\\]\\* from Aspire AppHost AppHost/\\[unsafe\\]\\*.csproj?');
        });

        test('re-resolves the AppHost immediately after confirmation', async () => {
            const resolver = new FakeTargetResolver();
            resolver.results = [
                {
                    resolved: true,
                    target: {
                        absolutePath: '/private/workspace/first/AppHost.csproj',
                        relativePath: 'first/AppHost.csproj',
                        displayPath: 'first/AppHost.csproj',
                    },
                },
                {
                    resolved: true,
                    target: {
                        absolutePath: '/private/workspace/second/AppHost.csproj',
                        relativePath: 'second/AppHost.csproj',
                        displayPath: 'second/AppHost.csproj',
                    },
                },
            ];
            const { service, resourceDebugger } = createService(resolver);
            const tool = new AspireResourceDebugLanguageModelTool(service);
            const input = createInput({ appHostPath: 'first/AppHost.csproj' });

            const prepared = await tool.prepareInvocation({ input: input as unknown as AspireResourceDebugToolInput }, new vscode.CancellationTokenSource().token);
            const result = readToolResultPayload(await tool.invoke(
                { input: input as unknown as AspireResourceDebugToolInput, toolInvocationToken: undefined },
                new vscode.CancellationTokenSource().token));

            assert.strictEqual(prepared.confirmationMessages?.message, 'Attach the debugger to resource api from Aspire AppHost first/AppHost.csproj?');
            assert.strictEqual(result.appHost, 'second/AppHost.csproj');
            assert.strictEqual(resourceDebugger.calls[0].appHost.absolutePath, '/private/workspace/second/AppHost.csproj');
        });
    });

    suite('cancellation and result mapping', () => {
        test('maps cancellation before and during resolution or debugging without side effects after cancellation', async () => {
            const before = createService();
            const beforeToken = new vscode.CancellationTokenSource();
            beforeToken.cancel();
            assert.strictEqual((await before.service.debug(createInput(), beforeToken.token)).outcome, 'cancelled');
            assert.strictEqual(before.targetResolver.calls, 0);

            const duringResolution = createService();
            const resolveToken = new vscode.CancellationTokenSource();
            duringResolution.targetResolver.onResolve = () => resolveToken.cancel();
            assert.strictEqual((await duringResolution.service.debug(createInput(), resolveToken.token)).outcome, 'cancelled');
            assert.strictEqual(duringResolution.resourceDebugger.calls.length, 0);

            const duringDebug = createService();
            const debugToken = new vscode.CancellationTokenSource();
            duringDebug.resourceDebugger.onDebug = () => debugToken.cancel();
            assert.strictEqual((await duringDebug.service.debug(createInput(), debugToken.token)).outcome, 'cancelled');
        });

        test('fails closed when disposal races AppHost resolution before an attach starts', async () => {
            const resolver = new FakeTargetResolver();
            const { service, resourceDebugger } = createService(resolver);
            resolver.onResolve = () => service.dispose();

            const result = await service.debug(createInput(), new vscode.CancellationTokenSource().token);

            assert.strictEqual(result.outcome, 'cancelled');
            assert.strictEqual(resourceDebugger.calls.length, 0);
        });

        test('cancels a resolver operation owned by the service when it is disposed', async () => {
            const resolver = new FakeTargetResolver();
            let markResolutionStarted: (() => void) | undefined;
            const resolutionStarted = new Promise<void>(resolve => {
                markResolutionStarted = resolve;
            });
            resolver.onResolve = token => new Promise<void>(resolve => {
                markResolutionStarted!();
                token.onCancellationRequested(resolve);
            });
            const { service, resourceDebugger } = createService(resolver);
            const callerCancellation = new vscode.CancellationTokenSource();

            try {
                const operation = service.debug(createInput(), callerCancellation.token);
                await resolutionStarted;
                service.dispose();

                assert.strictEqual((await operation).outcome, 'cancelled');
                assert.strictEqual(resourceDebugger.calls.length, 0);
                assert.notStrictEqual(resolver.tokens[0], callerCancellation.token);
                assert.strictEqual(resolver.tokens[0].isCancellationRequested, true);
            }
            finally {
                callerCancellation.dispose();
            }
        });

        test('cancels an in-flight debugger operation when the service is disposed', async () => {
            const { service, resourceDebugger } = createService();
            let markDebugStarted: (() => void) | undefined;
            const debugStarted = new Promise<void>(resolve => {
                markDebugStarted = resolve;
            });
            resourceDebugger.onDebug = request => new Promise<void>(resolve => {
                markDebugStarted!();
                request.cancellationToken?.onCancellationRequested(resolve);
            });
            const callerCancellation = new vscode.CancellationTokenSource();

            try {
                const operation = service.debug(createInput(), callerCancellation.token);
                await debugStarted;
                service.dispose();

                assert.strictEqual((await operation).outcome, 'cancelled');
                assert.strictEqual(resourceDebugger.calls.length, 1);
                assert.notStrictEqual(resourceDebugger.calls[0].cancellationToken, callerCancellation.token);
                assert.strictEqual(resourceDebugger.calls[0].cancellationToken?.isCancellationRequested, true);
            }
            finally {
                callerCancellation.dispose();
            }
        });

        test('maps every bounded resource debug result', async () => {
            const cases: Array<{
                readonly result: ResourceDebugResult;
                readonly expected: Pick<AspireResourceDebugToolResult, 'success' | 'outcome' | 'effectiveStrategy' | 'controller' | 'provider' | 'errorKind'>;
            }> = [
                { result: { outcome: 'started', providerId: 'dotnet' }, expected: { success: true, outcome: 'started', effectiveStrategy: 'attach', controller: 'editor', provider: 'dotnet', errorKind: undefined } },
                { result: { outcome: 'alreadyDebugging' }, expected: { success: true, outcome: 'alreadyDebugging', effectiveStrategy: 'attach', controller: 'editor', provider: undefined, errorKind: undefined } },
                { result: { outcome: 'appHostNotFound' }, expected: { success: false, outcome: 'appHostNotFound', effectiveStrategy: 'none', controller: 'none', provider: undefined, errorKind: undefined } },
                { result: { outcome: 'resourceNotFound' }, expected: { success: false, outcome: 'resourceNotFound', effectiveStrategy: 'none', controller: 'none', provider: undefined, errorKind: undefined } },
                { result: { outcome: 'unsupportedResource' }, expected: { success: false, outcome: 'unsupportedResource', effectiveStrategy: 'none', controller: 'none', provider: undefined, errorKind: undefined } },
                { result: { outcome: 'resourceNotRunning' }, expected: { success: false, outcome: 'resourceNotRunning', effectiveStrategy: 'none', controller: 'none', provider: undefined, errorKind: undefined } },
                { result: { outcome: 'cancelled' }, expected: { success: false, outcome: 'cancelled', effectiveStrategy: 'none', controller: 'none', provider: undefined, errorKind: undefined } },
                ...(['resourceSnapshotFailed', 'providerResolutionFailed', 'configurationFailed', 'debuggerStartDeclined', 'debuggerStartFailed', 'unexpected'] as const).map(errorKind => ({
                    result: { outcome: 'error', errorKind } as ResourceDebugResult,
                    expected: { success: false, outcome: 'error' as const, effectiveStrategy: 'none' as const, controller: 'none' as const, provider: undefined, errorKind },
                })),
            ];

            for (const testCase of cases) {
                const { service, resourceDebugger } = createService();
                resourceDebugger.result = testCase.result;

                const result = await service.debug(createInput(), new vscode.CancellationTokenSource().token);

                assert.deepStrictEqual(
                    {
                        success: result.success,
                        outcome: result.outcome,
                        effectiveStrategy: result.effectiveStrategy,
                        controller: result.controller,
                        provider: result.provider,
                        errorKind: result.errorKind,
                    },
                    testCase.expected);
            }
        });

        test('returns only safe C# and Go debugger requirements', async () => {
            for (const [id, label] of [
                ['ms-dotnettools.csharp', 'C#'],
                ['golang.go', 'Go'],
            ]) {
                const { service, resourceDebugger } = createService();
                resourceDebugger.result = {
                    outcome: 'debuggerExtensionMissing',
                    debuggerExtensions: [{ id, label, installMessage: 'token=super-secret /private/debug.json --args bad' }],
                };

                const result = await service.debug(createInput(), new vscode.CancellationTokenSource().token);

                assert.deepStrictEqual(result.debuggerExtensions, [{ id, label }]);
                assert.strictEqual(result.provider, undefined);
                assert.strictEqual(result.success, false);
                assert.strictEqual(JSON.stringify(result).includes('super-secret'), false);
            }
        });

        test('converts unexpected exceptions to a bounded, valid JSON result without sensitive data', async () => {
            const { service, resourceDebugger } = createService();
            resourceDebugger.error = new Error('token=super-secret pid=42 /private/debug.json --configuration {"process":"dotnet"} https://private.example args=unsafe');
            const tool = new AspireResourceDebugLanguageModelTool(service);

            const languageModelResult = await tool.invoke(
                { input: createInput() as unknown as AspireResourceDebugToolInput, toolInvocationToken: undefined },
                new vscode.CancellationTokenSource().token);
            const payload = readToolResultPayload(languageModelResult);
            const serialized = JSON.stringify(payload);

            assert.deepStrictEqual(
                {
                    outcome: payload.outcome,
                    success: payload.success,
                    appHost: payload.appHost,
                    effectiveStrategy: payload.effectiveStrategy,
                    controller: payload.controller,
                },
                {
                    outcome: 'failed',
                    success: false,
                    appHost: safeAppHostPath,
                    effectiveStrategy: 'none',
                    controller: 'none',
                });
            for (const forbidden of ['super-secret', '/private/', 'pid=42', 'dotnet', 'private.example', 'args=unsafe', 'debug.json']) {
                assert.strictEqual(serialized.includes(forbidden), false, `Tool result leaked ${forbidden}.`);
            }
            assert.deepStrictEqual(JSON.parse(serialized), payload);
        });
    });

    test('uses the neutral AppHost target resolver contract without importing lifecycle policy', () => {
        assert.strictEqual(typeof AppHostTargetResolverService.prototype.resolveTarget, 'function');
        assert.strictEqual(AppHostLifecycleToolService.prototype.isPrototypeOf(AppHostTargetResolverService.prototype), false);
    });
});
