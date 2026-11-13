import * as assert from 'assert';
import {
    resolveDashboardLaunchBehavior,
    resolveExplicitDashboardLaunchBehavior,
    type DashboardLaunchBehavior,
} from '../debugger/session/dashboardLauncher';
import { createAspireConfiguration } from './helpers/editorAssistanceTestSupport';

suite('Dashboard launch behavior resolution', () => {

    suite('resolveExplicitDashboardLaunchBehavior (explicit handoff, e.g. the Open Dashboard tool)', () => {
        test('falls back to the external browser when aspire.dashboardBrowser is entirely unset', () => {
            const resolved = resolveExplicitDashboardLaunchBehavior(createAspireConfiguration());
            assert.deepStrictEqual(resolved, { behavior: 'openExternalBrowser', source: 'default' });
        });

        test('falls back to the external browser when aspire.dashboardBrowser is explicitly "none" (global configuration)', () => {
            const resolved = resolveExplicitDashboardLaunchBehavior(
                createAspireConfiguration({ dashboardBrowser: 'none' }));
            assert.deepStrictEqual(resolved, { behavior: 'openExternalBrowser', source: 'globalConfiguration' });
        });

        test('falls back to the external browser when the debug configuration explicitly requests "none"', () => {
            const resolved = resolveExplicitDashboardLaunchBehavior(
                createAspireConfiguration(),
                'none');
            assert.deepStrictEqual(resolved, { behavior: 'openExternalBrowser', source: 'debugConfiguration' });
        });

        test('falls back to the external browser when the debug configuration requests "none" even if global configuration is also "none"', () => {
            const resolved = resolveExplicitDashboardLaunchBehavior(
                createAspireConfiguration({ dashboardBrowser: 'none' }),
                'none');
            assert.deepStrictEqual(resolved, { behavior: 'openExternalBrowser', source: 'debugConfiguration' });
        });

        test('uses the global named presentation when the debug configuration requests "none": "none" is not itself a presentation at this precedence layer', () => {
            const resolved = resolveExplicitDashboardLaunchBehavior(
                createAspireConfiguration({ dashboardBrowser: 'integratedBrowser' }),
                'none');
            assert.deepStrictEqual(resolved, { behavior: 'integratedBrowser', source: 'globalConfiguration' });
        });

        test('falls back to the external browser for a legacy auto-launch configuration with no new preference', () => {
            const resolved = resolveExplicitDashboardLaunchBehavior(
                createAspireConfiguration({ enableAspireDashboardAutoLaunch: 'launch' }));
            assert.deepStrictEqual(resolved, { behavior: 'openExternalBrowser', source: 'legacyConfiguration' });
        });

        test('falls back to the external browser for a legacy "off" auto-launch configuration', () => {
            const resolved = resolveExplicitDashboardLaunchBehavior(
                createAspireConfiguration({ enableAspireDashboardAutoLaunch: 'off' }));
            assert.deepStrictEqual(resolved, { behavior: 'openExternalBrowser', source: 'legacyConfiguration' });
        });

        test('falls back to the external browser for a legacy boolean `true` auto-launch value (maps to "launch")', () => {
            const resolved = resolveExplicitDashboardLaunchBehavior(
                createAspireConfiguration({ enableAspireDashboardAutoLaunch: true }));
            assert.deepStrictEqual(resolved, { behavior: 'openExternalBrowser', source: 'legacyConfiguration' });
        });

        test('keeps the legacy boolean `false` auto-launch value mapped to "notification" unchanged', () => {
            const resolved = resolveExplicitDashboardLaunchBehavior(
                createAspireConfiguration({ enableAspireDashboardAutoLaunch: false }));
            assert.deepStrictEqual(resolved, { behavior: 'notification', source: 'legacyConfiguration' });
        });


        test('keeps every explicit named browser choice from the debug configuration unchanged', () => {
            const namedChoices: DashboardLaunchBehavior[] = [
                'integratedBrowser',
                'openExternalBrowser',
                'debugChrome',
                'debugEdge',
                'debugFirefox',
                'notification',
            ];

            for (const choice of namedChoices) {
                const resolved = resolveExplicitDashboardLaunchBehavior(createAspireConfiguration(), choice);
                assert.deepStrictEqual(
                    resolved,
                    { behavior: choice, source: 'debugConfiguration' },
                    `Expected the debug-configuration choice "${choice}" to pass through unchanged.`);
            }
        });

        test('keeps every explicit named browser choice from global configuration unchanged', () => {
            const namedChoices: DashboardLaunchBehavior[] = [
                'integratedBrowser',
                'openExternalBrowser',
                'debugChrome',
                'debugEdge',
                'debugFirefox',
                'notification',
            ];

            for (const choice of namedChoices) {
                const resolved = resolveExplicitDashboardLaunchBehavior(
                    createAspireConfiguration({ dashboardBrowser: choice }));
                assert.deepStrictEqual(
                    resolved,
                    { behavior: choice, source: 'globalConfiguration' },
                    `Expected the global-configuration choice "${choice}" to pass through unchanged.`);
            }
        });

        test('keeps the legacy "notification" auto-launch choice unchanged', () => {
            const resolved = resolveExplicitDashboardLaunchBehavior(
                createAspireConfiguration({ enableAspireDashboardAutoLaunch: 'notification' }));
            assert.deepStrictEqual(resolved, { behavior: 'notification', source: 'legacyConfiguration' });
        });

        test('reports the fallback source as provenance without letting it change the presentation', () => {
            // Every configuration that reaches the fallback opens the same external browser. The
            // source only records the most specific layer the user configured and this fallback
            // did not honor, so a "legacyConfiguration" source here never means the legacy
            // setting selected the browser.
            const fallbackCases: Array<{
                readonly values: Readonly<Record<string, unknown>>;
                readonly debugConfigurationValue?: unknown;
                readonly expectedSource: string;
            }> = [
                { values: {}, debugConfigurationValue: 'none', expectedSource: 'debugConfiguration' },
                { values: { dashboardBrowser: 'none' }, expectedSource: 'globalConfiguration' },
                { values: { enableAspireDashboardAutoLaunch: 'launch' }, expectedSource: 'legacyConfiguration' },
                { values: { enableAspireDashboardAutoLaunch: 'off' }, expectedSource: 'legacyConfiguration' },
                { values: {}, expectedSource: 'default' },
            ];

            for (const { values, debugConfigurationValue, expectedSource } of fallbackCases) {
                assert.deepStrictEqual(
                    resolveExplicitDashboardLaunchBehavior(
                        createAspireConfiguration(values),
                        debugConfigurationValue),
                    { behavior: 'openExternalBrowser', source: expectedSource },
                    `Expected ${JSON.stringify({ values, debugConfigurationValue })} to fall back to the external browser.`);
            }
        });

        test('only routes a legacy source to the legacy setting when the legacy value really selected the presentation', () => {
            // `openDashboardLaunchBehaviorSettings` opens `aspire.enableAspireDashboardAutoLaunch`
            // for a legacy source, and only a notification presentation ever hands it a source.
            // That notification case is the one where the legacy value is genuinely responsible.
            const notification = resolveExplicitDashboardLaunchBehavior(
                createAspireConfiguration({ enableAspireDashboardAutoLaunch: 'notification' }));
            assert.deepStrictEqual(notification, { behavior: 'notification', source: 'legacyConfiguration' });

            const legacyLaunch = resolveExplicitDashboardLaunchBehavior(
                createAspireConfiguration({ enableAspireDashboardAutoLaunch: 'launch' }));
            assert.strictEqual(legacyLaunch.behavior, 'openExternalBrowser');
            assert.notStrictEqual(legacyLaunch.behavior, 'notification');
        });
    });

    suite('resolveDashboardLaunchBehavior (automatic launch) semantics remain unchanged', () => {
        test('an entirely unset configuration still disables automatic launch (behavior "none")', () => {
            const resolved = resolveDashboardLaunchBehavior(createAspireConfiguration());
            assert.deepStrictEqual(resolved, { behavior: 'none', source: 'default' });
        });

        test('an explicit "none" still disables automatic launch', () => {
            const resolved = resolveDashboardLaunchBehavior(
                createAspireConfiguration({ dashboardBrowser: 'none' }));
            assert.deepStrictEqual(resolved, { behavior: 'none', source: 'globalConfiguration' });
        });

        test('an explicit "none" from the debug configuration still disables automatic launch', () => {
            const resolved = resolveDashboardLaunchBehavior(
                createAspireConfiguration(),
                'none');
            assert.deepStrictEqual(resolved, { behavior: 'none', source: 'debugConfiguration' });
        });

        test('a legacy auto-launch configuration with no new preference still falls back to the integrated browser', () => {
            const resolved = resolveDashboardLaunchBehavior(
                createAspireConfiguration({ enableAspireDashboardAutoLaunch: 'launch' }));
            assert.deepStrictEqual(resolved, { behavior: 'integratedBrowser', source: 'legacyConfiguration' });
        });

        test('a legacy boolean `true` auto-launch value maps to "launch" and still falls back to the integrated browser', () => {
            const resolved = resolveDashboardLaunchBehavior(
                createAspireConfiguration({ enableAspireDashboardAutoLaunch: true }));
            assert.deepStrictEqual(resolved, { behavior: 'integratedBrowser', source: 'legacyConfiguration' });
        });

        test('a legacy boolean `false` auto-launch value maps to "notification"', () => {
            const resolved = resolveDashboardLaunchBehavior(
                createAspireConfiguration({ enableAspireDashboardAutoLaunch: false }));
            assert.deepStrictEqual(resolved, { behavior: 'notification', source: 'legacyConfiguration' });
        });

        test('every explicit named browser choice still passes through unchanged', () => {
            const namedChoices: DashboardLaunchBehavior[] = [
                'integratedBrowser',
                'openExternalBrowser',
                'debugChrome',
                'debugEdge',
                'debugFirefox',
                'notification',
            ];

            for (const choice of namedChoices) {
                const resolved = resolveDashboardLaunchBehavior(
                    createAspireConfiguration({ dashboardBrowser: choice }));
                assert.deepStrictEqual(
                    resolved,
                    { behavior: choice, source: 'globalConfiguration' },
                    `Expected the global-configuration choice "${choice}" to pass through unchanged.`);
            }
        });
    });
});
