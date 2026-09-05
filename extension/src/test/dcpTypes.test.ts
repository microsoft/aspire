import * as assert from 'assert';
import * as path from 'path';

import {
    getLaunchConfigurationExecutablePaths,
    getLaunchConfigurationTargetPath,
    type ExecutableLaunchConfiguration,
} from '../dcp/types';

suite('DCP launch configuration types', () => {
    test('derives resource target paths only from stable structured launch fields', () => {
        const cases: readonly [unknown, string | undefined][] = [
            [{ type: 'project', project_path: '/workspace/Api/Api.csproj' }, '/workspace/Api/Api.csproj'],
            [{ type: 'azure-functions', project_path: '/workspace/Functions/Functions.csproj' }, '/workspace/Functions/Functions.csproj'],
            [{ type: 'maui', project_path: '/workspace/App/App.csproj' }, '/workspace/App/App.csproj'],
            [{
                type: 'node',
                script_path: '/workspace/web/server.js',
                working_directory: '/workspace/web',
            }, '/workspace/web/server.js'],
            [{
                type: 'bun',
                script_path: '/workspace/bun/server.ts',
                working_directory: '/workspace/bun',
            }, '/workspace/bun/server.ts'],
            [{
                type: 'python',
                program_path: '/workspace/python/main.py',
                working_directory: '/workspace/python',
            }, '/workspace/python/main.py'],
            [{
                type: 'go',
                program: '/workspace/go/cmd/api',
                working_directory: '/workspace/go',
            }, '/workspace/go/cmd/api'],
            [{
                type: 'rust',
                cargo: { executable_path: '/workspace/rust/target/debug/api' },
                working_directory: '/workspace/rust',
            }, '/workspace/rust/target/debug/api'],
            [{
                type: 'node',
                working_directory: '/workspace/package-manager-app',
            }, '/workspace/package-manager-app'],
            [{
                type: 'python',
                module: 'private.module',
                working_directory: '/workspace/python-module',
            }, '/workspace/python-module'],
            [{
                type: 'go',
                working_directory: '/workspace/go-package',
            }, '/workspace/go-package'],
            [{
                type: 'rust',
                working_directory: '/workspace/rust-package',
            }, '/workspace/rust-package'],
            [{
                type: 'java',
                main_class: 'com.example.Api',
                working_directory: '/workspace/java-api',
                build_tool: 'maven',
            }, '/workspace/java-api'],
            [{
                // The IDE resolves the entry point itself, so there is no main_class to fall back on.
                type: 'java',
                project_name: 'api',
                working_directory: '/workspace/java-imported',
            }, '/workspace/java-imported'],
            [{
                // A module-qualified class name is not a path and must never be treated as one.
                type: 'java',
                main_class: 'api/com.example.Api',
            }, undefined],
            [{
                type: 'node',
                runtime_executable: '/private/runtime/node',
                args: ['/workspace/forged-from-args'],
                env: { PRIVATE_TARGET: '/workspace/forged-from-env' },
            }, undefined],
            [{
                type: 'browser',
                name: '/workspace/forged-from-session-name',
                args: ['/workspace/forged-from-args'],
                env: { PRIVATE_TARGET: '/workspace/forged-from-env' },
            }, undefined],
        ];

        for (const [configuration, expected] of cases) {
            assert.strictEqual(
                getLaunchConfigurationTargetPath(configuration as ExecutableLaunchConfiguration),
                expected);
        }
    });

    test('derives resource executable paths without inspecting args or environment', () => {
        const pythonScriptsDirectory = path.join('/workspace/python/.venv', process.platform === 'win32' ? 'Scripts' : 'bin');
        const pythonInterpreterPath = path.join(
            pythonScriptsDirectory,
            process.platform === 'win32' ? 'python.exe' : 'python');
        const pythonEntrypointPath = path.join(
            pythonScriptsDirectory,
            process.platform === 'win32' ? 'pytest.exe' : 'pytest');
        const cases: readonly [unknown, readonly string[]][] = [
            [{
                type: 'node',
                script_path: '/workspace/web/server.js',
                runtime_executable: '/usr/local/bin/node',
            }, ['/usr/local/bin/node']],
            [{
                type: 'bun',
                working_directory: '/workspace/bun',
                runtime_executable: 'bun',
            }, ['bun']],
            [{
                type: 'python',
                program_path: '/workspace/python/main.py',
                interpreter_path: pythonInterpreterPath,
            }, [pythonInterpreterPath]],
            [{
                type: 'python',
                module: 'pytest',
                working_directory: '/workspace/python',
                interpreter_path: pythonInterpreterPath,
            }, [
                pythonInterpreterPath,
                pythonEntrypointPath,
            ]],
            [{
                type: 'go',
                program: '/workspace/go/cmd/api',
            }, ['go']],
            [{
                type: 'rust',
                cargo: { executable_path: '/workspace/rust/target/debug/api' },
            }, ['cargo']],
            [{
                type: 'java',
                main_class: 'com.example.Api',
                working_directory: '/workspace/java-api',
            }, ['java']],
            [{
                // Wrapper invocations run through sh/cmd, so they correlate on the working
                // directory instead of a command that every wrapper resource would share.
                type: 'java',
                working_directory: '/workspace/java-gradle',
                build_tool: 'gradle',
            }, ['java']],
            [{
                type: 'node',
                script_path: '/workspace/web/server.js',
                args: ['/workspace/forged-from-args'],
                env: { PRIVATE_TARGET: '/workspace/forged-from-env' },
            }, []],
            [{
                type: 'browser',
                name: '/workspace/forged-from-session-name',
                args: ['/workspace/forged-from-args'],
                env: { PRIVATE_TARGET: '/workspace/forged-from-env' },
            }, []],
        ];

        for (const [configuration, expected] of cases) {
            assert.deepStrictEqual(
                getLaunchConfigurationExecutablePaths(configuration as ExecutableLaunchConfiguration),
                expected);
        }
    });
});
