import * as assert from 'assert';
import * as fs from 'fs';
import * as path from 'path';
import { formatText } from '../utils/strings';

suite('utils/strings tests', () => {
	test('formatText formats correctly ', () => {
        const input = 'This is a test :ice: :rocket: :bug: :microscope: :linked_paperclips: :chart_increasing: :chart_decreasing: :locked_with_key: :play_button: :check_mark: :cross_mark: :hammer_and_wrench:';
        const expectedOutput = 'This is a test 🧊 🚀 🐛 🔬 🔗 📈 📉 🔒 ▶️ ✅ ❌ 🛠️';
        const result = formatText(input);
        assert.strictEqual(result, expectedOutput);

        const inputWithUnknownEmoji = 'This is a test :unknown_emoji:';
        const expectedOutputWithUnknownEmoji = 'This is a test :unknown_emoji:';
        const resultWithUnknownEmoji = formatText(inputWithUnknownEmoji);
        assert.strictEqual(resultWithUnknownEmoji, expectedOutputWithUnknownEmoji);

        const inputWithNoEmojis = 'This is a test without emojis.';
        const expectedOutputWithNoEmojis = 'This is a test without emojis.';
        const resultWithNoEmojis = formatText(inputWithNoEmojis);
        assert.strictEqual(resultWithNoEmojis, expectedOutputWithNoEmojis);
	});


    test('copy AppHost path loc strings have package nls entries', () => {
        const extensionRoot = path.resolve(__dirname, '..', '..');
        const stringsSource = fs.readFileSync(path.join(extensionRoot, 'src', 'loc', 'strings.ts'), 'utf8');
        const packageNls = JSON.parse(fs.readFileSync(path.join(extensionRoot, 'package.nls.json'), 'utf8')) as Record<string, string>;

        const expectedStrings = {
            appHostPathCopiedToClipboard: 'AppHost path copied to clipboard.',
            appHostPathInvalid: 'Could not determine the AppHost path to copy.',
        };

        for (const [name, value] of Object.entries(expectedStrings)) {
            assert.ok(stringsSource.includes(`export const ${name} = vscode.l10n.t('${value}');`));
            assert.strictEqual(packageNls[`aspire-vscode.strings.${name}`], value);
        }
    });
});