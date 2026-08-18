import * as assert from 'assert';
import * as fs from 'fs';
import * as path from 'path';
import { launchingWithAppHost, launchingWithDirectory } from '../loc/strings';
import { collapseWhitespace, escapeCodicons, formatText } from '../utils/strings';

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

    test('collapseWhitespace renders multi-line CLI status as a single line', () => {
        assert.strictEqual(collapseWhitespace('  Building\n  the AppHost\r\n\tnow  '), 'Building the AppHost now');
        assert.strictEqual(collapseWhitespace('Building...'), 'Building...');
        assert.strictEqual(collapseWhitespace('   '), '');
    });

    test('escapeCodicons stops untrusted text from injecting status bar icons', () => {
        // VS Code renders `$(name)` as an icon in the status bar and in window progress, so CLI
        // controlled status text could otherwise draw an arbitrary (or spinning) icon.
        assert.strictEqual(escapeCodicons('Building $(error) now'), 'Building \\$(error) now');
        assert.strictEqual(escapeCodicons('Building $(myExt-Icon~spin) now'), 'Building \\$(myExt-Icon~spin) now');
        assert.strictEqual(escapeCodicons('$(sync~spin)$(bug)'), '\\$(sync~spin)\\$(bug)');
        // An already escaped sequence must not be double escaped, otherwise the backslash shows up.
        assert.strictEqual(escapeCodicons('Building \\$(error) now'), 'Building \\$(error) now');
        assert.strictEqual(escapeCodicons('Running $(step one)'), 'Running $(step one)');
        assert.strictEqual(escapeCodicons('Cost is $5 (approx)'), 'Cost is $5 (approx)');
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
            // Match the declaration tolerantly so formatting differences do not matter. The literal
            // value is regex-escaped so the test still fails if the registered string changes.
            const escapedValue = value.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
            const declaration = new RegExp(
                `export\\s+const\\s+${name}\\s*=\\s*vscode\\.l10n\\.t\\(\\s*(['"\`])${escapedValue}\\1\\s*\\)`);
            assert.match(stringsSource, declaration, `Expected ${name} to be registered in strings.ts with the value "${value}".`);
            assert.strictEqual(packageNls[`aspire-vscode.strings.${name}`], value);
        }
    });

    test('Rust loc strings are present in package.nls.json and the generated XLF catalog', () => {
        // package.nls.json is the only input to the XLF catalog (see gulpfile.js), so a string that
        // only exists in strings.ts is shipped untranslated. Guard the Rust debugger strings, which
        // are the newest additions and the easiest to forget.
        const extensionRoot = path.resolve(__dirname, '..', '..');
        const stringsSource = fs.readFileSync(path.join(extensionRoot, 'src', 'loc', 'strings.ts'), 'utf8');
        const packageNls = JSON.parse(fs.readFileSync(path.join(extensionRoot, 'package.nls.json'), 'utf8')) as Record<string, string>;
        const xlf = fs.readFileSync(path.join(extensionRoot, 'loc', 'xlf', 'aspire-vscode.xlf'), 'utf8');

        const declarationPattern = /export\s+const\s+(rust[A-Za-z0-9_]*)\s*=\s*(?:\([^)]*\)\s*=>\s*)?vscode\.l10n\.t\(/g;
        const names = [...stringsSource.matchAll(declarationPattern)].map(match => match[1]);
        assert.ok(names.includes('rustDebuggerExtensionNotInstalled'), 'Expected rustDebuggerExtensionNotInstalled to be declared in strings.ts.');

        const missingFromNls = names.filter(name => packageNls[`aspire-vscode.strings.${name}`] === undefined);
        assert.deepStrictEqual(missingFromNls, [], 'Every rust* loc string needs an aspire-vscode.strings.* entry in package.nls.json.');

        const missingFromXlf = names.filter(name => !xlf.includes(`<trans-unit id="aspire-vscode.strings.${name}">`));
        assert.deepStrictEqual(missingFromXlf, [], 'Regenerate loc/xlf/aspire-vscode.xlf with "yarn run localize" after adding package.nls.json entries.');
    });

    test('Java loc strings are present in package.nls.json and the generated XLF catalog', () => {
        // Same guard as the Rust strings above: package.nls.json is the only input to the XLF
        // catalog (see gulpfile.js), so a Java string that only exists in strings.ts ships
        // untranslated.
        const extensionRoot = path.resolve(__dirname, '..', '..');
        const stringsSource = fs.readFileSync(path.join(extensionRoot, 'src', 'loc', 'strings.ts'), 'utf8');
        const packageNls = JSON.parse(fs.readFileSync(path.join(extensionRoot, 'package.nls.json'), 'utf8')) as Record<string, string>;
        const xlf = fs.readFileSync(path.join(extensionRoot, 'loc', 'xlf', 'aspire-vscode.xlf'), 'utf8');

        // The Spring Boot Dashboard lens strings are matched explicitly because they belong to the Java
        // experience but are named after the code lens they render in, so a java* prefix scan misses them.
        const declarationPattern = /export\s+const\s+(java[A-Za-z0-9_]*|codeLensSpringBoot[A-Za-z0-9_]*)\s*=\s*(?:\([^)]*\)\s*=>\s*)?vscode\.l10n\.t\(/g;
        const names = [...stringsSource.matchAll(declarationPattern)].map(match => match[1]);
        assert.ok(names.includes('javaDisplayName'), 'Expected javaDisplayName to be declared in strings.ts.');
        assert.ok(names.includes('javaLabel'), 'Expected javaLabel to be declared in strings.ts.');
        assert.ok(
            names.includes('codeLensSpringBootDashboardBypassesAspire'),
            'Expected codeLensSpringBootDashboardBypassesAspire to be declared in strings.ts.');

        const missingFromNls = names.filter(name => packageNls[`aspire-vscode.strings.${name}`] === undefined);
        assert.deepStrictEqual(missingFromNls, [], 'Every Java loc string needs an aspire-vscode.strings.* entry in package.nls.json.');

        const missingFromXlf = names.filter(name => !xlf.includes(`<trans-unit id="aspire-vscode.strings.${name}">`));
        assert.deepStrictEqual(missingFromXlf, [], 'Regenerate loc/xlf/aspire-vscode.xlf with "yarn run localize" after adding package.nls.json entries.');
    });
});

suite('loc/strings tests', () => {
	test('formats launch messages with the session type', () => {
		assert.deepStrictEqual(
			[
				launchingWithAppHost('debug', '/workspace/apphost.cs'),
				launchingWithAppHost('run', '/workspace/apphost.cs'),
				launchingWithDirectory('debug', '/workspace'),
				launchingWithDirectory('run', '/workspace'),
			],
			[
				'Launching Aspire debug session for AppHost /workspace/apphost.cs...',
				'Launching Aspire run session for AppHost /workspace/apphost.cs...',
				'Launching Aspire debug session using directory /workspace: attempting to determine effective AppHost...',
				'Launching Aspire run session using directory /workspace: attempting to determine effective AppHost...',
			]);
	});

	test('registers complete launch messages for localization', () => {
		const extensionRoot = path.resolve(__dirname, '..', '..');
		const packageNls = JSON.parse(fs.readFileSync(path.join(extensionRoot, 'package.nls.json'), 'utf8')) as Record<string, string>;

		assert.deepStrictEqual(
			{
				launchingWithDirectory: packageNls['aspire-vscode.strings.launchingWithDirectory'],
				launchingWithAppHost: packageNls['aspire-vscode.strings.launchingWithAppHost'],
				launchingRunWithDirectory: packageNls['aspire-vscode.strings.launchingRunWithDirectory'],
				launchingRunWithAppHost: packageNls['aspire-vscode.strings.launchingRunWithAppHost'],
			},
			{
				launchingWithDirectory: 'Launching Aspire debug session using directory {0}: attempting to determine effective AppHost...',
				launchingWithAppHost: 'Launching Aspire debug session for AppHost {0}...',
				launchingRunWithDirectory: 'Launching Aspire run session using directory {0}: attempting to determine effective AppHost...',
				launchingRunWithAppHost: 'Launching Aspire run session for AppHost {0}...',
			});
	});

    test('no new loc string ships without a package.nls.json entry', () => {
        // The two guards above only cover the rust*/java* prefixes, so the configuredCliPath* strings
        // were added, shipped English-only, and passed CI - package.nls.json is the only input to the
        // XLF catalog (see gulpfile.js), so a string that exists only in strings.ts never reaches
        // translators. This guard covers every vscode.l10n.t export instead of a prefix, so the next
        // omission fails here rather than in a localization drop.
        //
        // The allowlist is the set that predates the guard. It is deliberately an explicit list rather
        // than a count: entries may be removed as strings are localized, but adding one means shipping
        // a user-visible string untranslated and should be a conscious decision in review.
        const knownUnlocalized = new Set([
        'appHostStoppingDescription',
        'aspireDashboard',
        'aspireDebugSessionNotInitialized',
        'browserDisplayName',
        'browserLabel',
        'codeLensCommand',
        'codeLensDebugPipelineStep',
        'codeLensOpenDashboard',
        'codeLensResourceFailedToStart',
        'codeLensResourceFailedToStartError',
        'codeLensResourceNotStarted',
        'codeLensResourceRunning',
        'codeLensResourceRunningError',
        'codeLensResourceRunningWarning',
        'codeLensResourceRuntimeUnhealthy',
        'codeLensResourceStarting',
        'codeLensResourceStopped',
        'codeLensResourceStoppedError',
        'codeLensResourceStoppedErrorWithExitCode',
        'codeLensResourceStoppedWithExitCode',
        'codeLensResourceStopping',
        'codeLensResourceValueMissing',
        'codeLensResourceWaiting',
        'codeLensRestart',
        'codeLensRustAppHostAlreadyRunning',
        'codeLensRustAppHostAlreadyRunningTooltip',
        'codeLensRustAppHostUseAspire',
        'codeLensRustAppHostUseAspireTooltip',
        'codeLensStart',
        'codeLensStop',
        'codeLensViewAppHostLogs',
        'codeLensViewLogs',
        'dashboardLabel',
        'defaultConfigurationName',
        'errorFetchingAppHosts',
        'errorMessage',
        'healthCheckDescription',
        'healthChecksLabel',
        'logFileLabel',
        'pidDescription',
        'resourceCommandLogOpenFailed',
        'resourceCommandOpenAppHostLog',
        'resourceCommandOpenCliLog',
        'resourceDescriptionExitCode',
        'resourceDescriptionHealth',
        'rpcServerAddressError',
        'settingsLabel',
        'tooltipEndpoints',
        'tooltipHealth',
        'tooltipState',
        'tooltipType',
        ]);

        const extensionRoot = path.resolve(__dirname, '..', '..');
        const stringsSource = fs.readFileSync(path.join(extensionRoot, 'src', 'loc', 'strings.ts'), 'utf8');
        const packageNls = JSON.parse(fs.readFileSync(path.join(extensionRoot, 'package.nls.json'), 'utf8')) as Record<string, string>;

        const declarationPattern = /export\s+const\s+([A-Za-z0-9_]+)\s*=\s*(?:\([^)]*\)\s*=>\s*)?vscode\.l10n\.t\(/g;
        const names = [...stringsSource.matchAll(declarationPattern)].map(match => match[1]);
        assert.ok(names.length > 200, `Expected the declaration scan to find the loc strings, found ${names.length}.`);

        const newlyMissing = names
            .filter(name => packageNls[`aspire-vscode.strings.${name}`] === undefined)
            .filter(name => !knownUnlocalized.has(name));
        assert.deepStrictEqual(newlyMissing, [], 'Add an aspire-vscode.strings.* entry to package.nls.json for these, then run "yarn run localize".');

        const localizedButAllowlisted = [...knownUnlocalized].filter(name => packageNls[`aspire-vscode.strings.${name}`] !== undefined);
        assert.deepStrictEqual(localizedButAllowlisted, [], 'These strings are localized now - remove them from knownUnlocalized.');
    });
});
