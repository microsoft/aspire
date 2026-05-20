import * as assert from 'assert';
import * as sinon from 'sinon';
import * as vscode from 'vscode';
import {
    buildResourceCommandCliArgs,
    collectResourceCommandArguments,
    confirmSecretArgumentWarning,
    getResourceCommandArgumentValidationMessage,
    hasDynamicResourceCommandArguments,
    hasSecretResourceCommandArguments,
    resourceCommandSecretWarningSuppressedKey,
    ResourceCommandArgumentValue,
} from '../views/ResourceCommandArguments';
import { ResourceCommandArgumentInputJson } from '../views/AppHostDataRepository';

function makeInput(overrides: Partial<ResourceCommandArgumentInputJson> = {}): ResourceCommandArgumentInputJson {
    return {
        name: 'message',
        label: 'Message',
        description: null,
        inputType: 'Text',
        required: false,
        placeholder: null,
        value: null,
        options: null,
        maxLength: null,
        ...overrides,
    };
}

class TestMemento implements vscode.Memento {
    private readonly values = new Map<string, unknown>();

    keys(): readonly string[] {
        return [...this.values.keys()];
    }

    get<T>(key: string): T | undefined;
    get<T>(key: string, defaultValue: T): T;
    get<T>(key: string, defaultValue?: T): T | undefined {
        return this.values.has(key) ? this.values.get(key) as T : defaultValue;
    }

    update(key: string, value: unknown): Thenable<void> {
        this.values.set(key, value);
        return Promise.resolve();
    }

    setKeysForSync(): void {
    }
}

suite('ResourceCommandArguments', () => {
    test('builds exact-name command options after delimiter', () => {
        const values: ResourceCommandArgumentValue[] = [
            { input: makeInput({ name: 'LogLevel', inputType: 'Choice' }), value: 'Debug' },
            { input: makeInput({ name: 'timeoutMilliseconds', inputType: 'Number' }), value: '1000' },
        ];

        assert.deepStrictEqual(buildResourceCommandCliArgs(values), [
            '--',
            '--LogLevel=Debug',
            '--timeoutMilliseconds=1000',
        ]);
    });

    test('encodes boolean values as single option tokens', () => {
        const values: ResourceCommandArgumentValue[] = [
            { input: makeInput({ name: 'enabled', inputType: 'Boolean' }), value: 'false' },
        ];

        assert.deepStrictEqual(buildResourceCommandCliArgs(values), ['--', '--enabled=false']);
    });

    test('submits choice option values instead of display labels', () => {
        const values: ResourceCommandArgumentValue[] = [
            {
                input: makeInput({
                    name: 'mode',
                    inputType: 'Choice',
                    options: {
                        'dry-run': 'Dry run',
                    },
                }),
                value: 'dry-run',
            },
        ];

        assert.deepStrictEqual(buildResourceCommandCliArgs(values), ['--', '--mode=dry-run']);
    });

    test('preserves spaces quotes and shell metacharacters as single argument values', () => {
        const value = 'hello world "quoted" $PATH ; & | < >';
        const values: ResourceCommandArgumentValue[] = [
            { input: makeInput({ name: 'message' }), value },
        ];

        assert.deepStrictEqual(buildResourceCommandCliArgs(values), ['--', `--message=${value}`]);
    });

    test('preserves option-like values without splitting them off as new options', () => {
        const values: ResourceCommandArgumentValue[] = [
            { input: makeInput({ name: 'message' }), value: '--help' },
            { input: makeInput({ name: 'flag', inputType: 'Choice' }), value: '-x' },
        ];

        assert.deepStrictEqual(buildResourceCommandCliArgs(values), [
            '--',
            '--message=--help',
            '--flag=-x',
        ]);
    });

    test('skips empty optional non-boolean inputs but submits booleans', () => {
        const values: ResourceCommandArgumentValue[] = [
            { input: makeInput({ name: 'optionalText', inputType: 'Text' }), value: '' },
            { input: makeInput({ name: 'optionalSecret', inputType: 'SecretText' }), value: '' },
            { input: makeInput({ name: 'optionalNumber', inputType: 'Number' }), value: '' },
            { input: makeInput({ name: 'requireHealthy', inputType: 'Boolean' }), value: 'false' },
        ];

        assert.deepStrictEqual(buildResourceCommandCliArgs(values), ['--', '--requireHealthy=false']);
    });

    test('submits empty value to clear a prefilled text or choice default', () => {
        const values: ResourceCommandArgumentValue[] = [
            { input: makeInput({ name: 'message', inputType: 'Text', value: 'previous' }), value: '' },
            { input: makeInput({ name: 'token', inputType: 'SecretText', value: 'old-token' }), value: '' },
            {
                input: makeInput({
                    name: 'mode',
                    inputType: 'Choice',
                    value: 'previous',
                    allowCustomChoice: true,
                }),
                value: '',
            },
        ];

        assert.deepStrictEqual(buildResourceCommandCliArgs(values), [
            '--',
            '--message=',
            '--token=',
            '--mode=',
        ]);
    });

    test('skips empty number even when a default value was prefilled', () => {
        const values: ResourceCommandArgumentValue[] = [
            { input: makeInput({ name: 'timeout', inputType: 'Number', value: '42' }), value: '' },
        ];

        assert.deepStrictEqual(buildResourceCommandCliArgs(values), []);
    });

    test('omits delimiter when no values are submitted', () => {
        const values: ResourceCommandArgumentValue[] = [
            { input: makeInput({ name: 'optional' }), value: '' },
        ];

        assert.deepStrictEqual(buildResourceCommandCliArgs(values), []);
    });

    test('validates required input', () => {
        const input = makeInput({ required: true });

        assert.strictEqual(getResourceCommandArgumentValidationMessage(input, '   '), 'This field is required.');
    });

    test('does not require boolean input text', () => {
        const input = makeInput({ inputType: 'Boolean', required: true });

        assert.strictEqual(getResourceCommandArgumentValidationMessage(input, ''), undefined);
    });

    test('validates invariant-culture numbers', () => {
        const input = makeInput({ inputType: 'Number' });

        assert.strictEqual(getResourceCommandArgumentValidationMessage(input, '1.5'), undefined);
        assert.strictEqual(getResourceCommandArgumentValidationMessage(input, '-1.5'), undefined);
        assert.strictEqual(getResourceCommandArgumentValidationMessage(input, '.5'), undefined);
        assert.strictEqual(getResourceCommandArgumentValidationMessage(input, '1e3'), undefined);
        assert.strictEqual(getResourceCommandArgumentValidationMessage(input, '+1.5E-2'), undefined);
        assert.strictEqual(getResourceCommandArgumentValidationMessage(input, '1,5'), 'Enter a number using invariant culture, for example 1, -1.5, or 1e3.');
    });

    test('validates maximum length', () => {
        const input = makeInput({ maxLength: 3 });

        assert.strictEqual(getResourceCommandArgumentValidationMessage(input, 'abcd'), 'Value must be 3 characters or fewer.');
    });

    test('detects enabled secret text arguments', () => {
        assert.strictEqual(hasSecretResourceCommandArguments({
            description: null,
            argumentInputs: [
                makeInput({ inputType: 'Text' }),
                makeInput({ inputType: 'SecretText' }),
            ],
        }), true);
    });

    test('ignores disabled secret text arguments', () => {
        assert.strictEqual(hasSecretResourceCommandArguments({
            description: null,
            argumentInputs: [
                makeInput({ inputType: 'SecretText', disabled: true }),
            ],
        }), false);
    });

    test('detects enabled dynamic arguments', () => {
        assert.strictEqual(hasDynamicResourceCommandArguments({
            description: null,
            argumentInputs: [
                makeInput({ name: 'browser' }),
                makeInput({ dynamicLoading: { alwaysLoadOnStart: true, dependsOnInputs: ['browser'] } }),
            ],
        }), true);
    });

    test('detects disabled dynamic arguments', () => {
        assert.strictEqual(hasDynamicResourceCommandArguments({
            description: null,
            argumentInputs: [
                makeInput({ dynamicLoading: { alwaysLoadOnStart: true }, disabled: true }),
            ],
        }), true);
    });

    test('blocks dynamic arguments when no loader is available', async () => {
        const warningStub = sinon.stub(vscode.window, 'showWarningMessage').resolves(undefined);
        const quickPickStub = sinon.stub(vscode.window, 'showQuickPick');

        try {
            const result = await collectResourceCommandArguments('open', {
                description: null,
                argumentInputs: [
                    makeInput({ inputType: 'SecretText' }),
                    makeInput({ inputType: 'Choice', dynamicLoading: { dependsOnInputs: ['message'] } }),
                ],
            });

            assert.strictEqual(result, undefined);
            assert.strictEqual(warningStub.calledOnce, true);
            assert.strictEqual(quickPickStub.called, false);
        }
        finally {
            warningStub.restore();
            quickPickStub.restore();
        }
    });

    test('loads dynamic arguments before prompting', async () => {
        const warningStub = sinon.stub(vscode.window, 'showWarningMessage').resolves(undefined);
        let loadCount = 0;

        try {
            const result = await collectResourceCommandArguments('open', {
                description: null,
                argumentInputs: [
                    makeInput({ inputType: 'Choice', dynamicLoading: { alwaysLoadOnStart: true } }),
                ],
            }, {
                loadDynamicArguments: async values => {
                    assert.deepStrictEqual(values, []);
                    loadCount++;
                    return [
                        makeInput({ inputType: 'Choice', disabled: true, dynamicLoading: { alwaysLoadOnStart: true } }),
                    ];
                },
            });

            assert.deepStrictEqual(result?.args, []);
            assert.strictEqual(result?.containsSecret, false);
            assert.strictEqual(loadCount, 1);
            assert.strictEqual(warningStub.called, false);
        }
        finally {
            warningStub.restore();
        }
    });

    test('loads initially disabled dynamic arguments before deciding whether to prompt', async () => {
        const warningStub = sinon.stub(vscode.window, 'showWarningMessage').resolves(undefined);
        let loadCount = 0;

        try {
            const result = await collectResourceCommandArguments('open', {
                description: null,
                argumentInputs: [
                    makeInput({ inputType: 'Choice', disabled: true, dynamicLoading: { alwaysLoadOnStart: true } }),
                ],
            }, {
                loadDynamicArguments: async values => {
                    assert.deepStrictEqual(values, []);
                    loadCount++;
                    return [
                        makeInput({ inputType: 'Choice', disabled: true, dynamicLoading: { alwaysLoadOnStart: true } }),
                    ];
                },
            });

            assert.deepStrictEqual(result?.args, []);
            assert.strictEqual(result?.containsSecret, false);
            assert.strictEqual(loadCount, 1);
            assert.strictEqual(warningStub.called, false);
        }
        finally {
            warningStub.restore();
        }
    });

    test('stores secret warning suppression when requested', async () => {
        const memento = new TestMemento();
        const suppressWarningItem: vscode.QuickPickItem & { suppressFutureWarnings: boolean } = { label: "Don't show again", suppressFutureWarnings: true };
        const quickPickStub = sinon.stub(vscode.window, 'showQuickPick').resolves(suppressWarningItem as never);
        const warningStub = sinon.stub(vscode.window, 'showWarningMessage');

        try {
            assert.strictEqual(await confirmSecretArgumentWarning(memento), true);
            assert.strictEqual(memento.get(resourceCommandSecretWarningSuppressedKey), true);
            assert.strictEqual(warningStub.called, false);
        }
        finally {
            quickPickStub.restore();
            warningStub.restore();
        }
    });

    test('skips secret warning when suppression is stored', async () => {
        const memento = new TestMemento();
        await memento.update(resourceCommandSecretWarningSuppressedKey, true);
        const quickPickStub = sinon.stub(vscode.window, 'showQuickPick');

        try {
            assert.strictEqual(await confirmSecretArgumentWarning(memento), true);
            assert.strictEqual(quickPickStub.called, false);
        }
        finally {
            quickPickStub.restore();
        }
    });
});
