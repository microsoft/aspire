import * as assert from 'assert';
import {
    buildResourceCommandCliArgs,
    getResourceCommandArgumentValidationMessage,
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

suite('ResourceCommandArguments', () => {
    test('builds exact-name command options after delimiter', () => {
        const values: ResourceCommandArgumentValue[] = [
            { input: makeInput({ name: 'LogLevel', inputType: 'Choice' }), value: 'Debug' },
            { input: makeInput({ name: 'timeoutMilliseconds', inputType: 'Number' }), value: '1000' },
        ];

        assert.deepStrictEqual(buildResourceCommandCliArgs(values), [
            '--',
            '--LogLevel',
            'Debug',
            '--timeoutMilliseconds',
            '1000',
        ]);
    });

    test('encodes boolean values as single option tokens', () => {
        const values: ResourceCommandArgumentValue[] = [
            { input: makeInput({ name: 'enabled', inputType: 'Boolean' }), value: 'false' },
        ];

        assert.deepStrictEqual(buildResourceCommandCliArgs(values), ['--', '--enabled=false']);
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

    test('validates invariant-culture numbers', () => {
        const input = makeInput({ inputType: 'Number' });

        assert.strictEqual(getResourceCommandArgumentValidationMessage(input, '1.5'), undefined);
        assert.strictEqual(getResourceCommandArgumentValidationMessage(input, '1,5'), 'Enter a number using digits and an optional decimal point.');
    });

    test('validates maximum length', () => {
        const input = makeInput({ maxLength: 3 });

        assert.strictEqual(getResourceCommandArgumentValidationMessage(input, 'abcd'), 'Value must be 3 characters or fewer.');
    });
});
