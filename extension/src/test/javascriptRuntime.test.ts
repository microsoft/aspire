import * as assert from 'assert';
import { launchMethodDirect, launchMethodPackageManager, resolveJavaScriptLaunchMethod } from '../debugger/languages/javascriptRuntime';
import { JavaScriptRuntimeLaunchConfiguration } from '../dcp/types';

suite('JavaScript Runtime Tests', () => {
    function config(launchMethod?: JavaScriptRuntimeLaunchConfiguration['launch_method']): JavaScriptRuntimeLaunchConfiguration {
        return {
            type: 'node',
            script_path: '/workspace/app/server.js',
            working_directory: '/workspace/app',
            launch_method: launchMethod
        };
    }

    test('explicit "package-manager" wins over an inferLegacy that returns "direct"', () => {
        const result = resolveJavaScriptLaunchMethod(config(launchMethodPackageManager), () => launchMethodDirect);

        assert.strictEqual(result, launchMethodPackageManager);
    });

    test('explicit "direct" wins over an inferLegacy that returns "package-manager"', () => {
        const result = resolveJavaScriptLaunchMethod(config(launchMethodDirect), () => launchMethodPackageManager);

        assert.strictEqual(result, launchMethodDirect);
    });

    test('undefined launch_method falls back to inferLegacy', () => {
        let inferred = false;
        const result = resolveJavaScriptLaunchMethod(config(undefined), () => {
            inferred = true;
            return launchMethodPackageManager;
        });

        assert.strictEqual(inferred, true);
        assert.strictEqual(result, launchMethodPackageManager);
    });

    test('unrecognized non-empty launch_method falls back to inferLegacy', () => {
        let inferred = false;
        // Cast through unknown because the contract type only permits the known values; this simulates
        // version skew where the hosting side emits a value the extension does not recognize.
        const drifted = { ...config(), launch_method: 'totally-bogus' as unknown as JavaScriptRuntimeLaunchConfiguration['launch_method'] };
        const result = resolveJavaScriptLaunchMethod(drifted, () => {
            inferred = true;
            return launchMethodDirect;
        });

        assert.strictEqual(inferred, true);
        assert.strictEqual(result, launchMethodDirect);
    });
});
