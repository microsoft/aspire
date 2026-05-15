import * as assert from 'assert';
import * as path from 'path';
import {
    determineBaseLaunchProfile,
    mergeEnvironmentVariables,
    determineArguments,
    determineWorkingDirectory,
    expandEnvironmentVariables
} from '../debugger/dotnetLaunchProfiles';
import { LaunchProfile, LaunchSettings } from '../debugger/dotnetLaunchSettings';
import { EnvVar, ProjectLaunchConfiguration } from '../dcp/types';

suite('Dotnet Launch Profile Tests', () => {
    suite('determineBaseLaunchProfile', () => {
        const sampleLaunchSettings: LaunchSettings = {
            profiles: {
                'Development': {
                    commandName: 'Project',
                    environmentVariables: {
                        ASPNETCORE_ENVIRONMENT: 'Development'
                    }
                },
                'Production': {
                    commandName: 'Project',
                    environmentVariables: {
                        ASPNETCORE_ENVIRONMENT: 'Production'
                    }
                },
                'IISExpress': {
                    commandName: 'IISExpress',
                    environmentVariables: {
                        ASPNETCORE_ENVIRONMENT: 'Development'
                    }
                }
            }
        };

        test('returns null when disable_launch_profile is true', () => {
            const launchConfig: ProjectLaunchConfiguration = {
                type: 'project',
                project_path: '/test/project.csproj',
                disable_launch_profile: true
            };

            const result = determineBaseLaunchProfile(launchConfig, sampleLaunchSettings);

            assert.strictEqual(result.profile, null);
            assert.strictEqual(result.profileName, null);
        });

        test('returns null when no launch settings available', () => {
            const launchConfig: ProjectLaunchConfiguration = {
                type: 'project',
                project_path: '/test/project.csproj'
            };

            const result = determineBaseLaunchProfile(launchConfig, null);

            assert.strictEqual(result.profile, null);
            assert.strictEqual(result.profileName, null);
        });

        test('returns explicit launch profile when specified and exists', () => {
            const launchConfig: ProjectLaunchConfiguration = {
                type: 'project',
                project_path: '/test/project.csproj',
                launch_profile: 'Development'
            };

            const result = determineBaseLaunchProfile(launchConfig, sampleLaunchSettings);

            assert.strictEqual(result.profileName, 'Development');
            assert.strictEqual(result.profile?.environmentVariables?.ASPNETCORE_ENVIRONMENT, 'Development');
        });

        test('returns null when explicit launch profile specified but does not exist', () => {
            const launchConfig: ProjectLaunchConfiguration = {
                type: 'project',
                project_path: '/test/project.csproj',
                launch_profile: 'NonExistent'
            };

            const result = determineBaseLaunchProfile(launchConfig, sampleLaunchSettings);

            assert.strictEqual(result.profile, null);
            assert.strictEqual(result.profileName, null);
        });

        test('returns first profile with commandName=Project when no explicit profile specified', () => {
            const launchConfig: ProjectLaunchConfiguration = {
                type: 'project',
                project_path: '/test/project.csproj'
            };

            const result = determineBaseLaunchProfile(launchConfig, sampleLaunchSettings);

            assert.strictEqual(result.profileName, 'Development');
            assert.strictEqual(result.profile?.commandName, 'Project');
            assert.strictEqual(result.profile?.environmentVariables?.ASPNETCORE_ENVIRONMENT, 'Development');
        });

        test('returns null when no profile has commandName=Project', () => {
            const settingsWithoutProject: LaunchSettings = {
                profiles: {
                    'IISExpress': {
                        commandName: 'IISExpress',
                        environmentVariables: {
                            ASPNETCORE_ENVIRONMENT: 'Development'
                        }
                    }
                }
            };

            const launchConfig: ProjectLaunchConfiguration = {
                type: 'project',
                project_path: '/test/project.csproj'
            };

            const result = determineBaseLaunchProfile(launchConfig, settingsWithoutProject);

            assert.strictEqual(result.profile, null);
            assert.strictEqual(result.profileName, null);
        });

        test('explicit profile takes precedence over default commandName=Project logic', () => {
            const launchConfig: ProjectLaunchConfiguration = {
                type: 'project',
                project_path: '/test/project.csproj',
                launch_profile: 'IISExpress'
            };

            const result = determineBaseLaunchProfile(launchConfig, sampleLaunchSettings);

            assert.strictEqual(result.profileName, 'IISExpress');
            assert.strictEqual(result.profile?.commandName, 'IISExpress');
        });
    });

    suite('mergeEnvironmentVariables', () => {
        test('merges environment variables with run session taking precedence', () => {
            const baseProfileEnv = {
                'VAR1': 'base1',
                'VAR2': 'base2',
                'VAR3': 'base3'
            };

            const runSessionEnv: EnvVar[] = [
                { name: 'VAR2', value: 'session2' },
                { name: 'VAR4', value: 'session4' }
            ];

            const result = mergeEnvironmentVariables(baseProfileEnv, undefined, runSessionEnv);

            assert.strictEqual(result.length, 4);

            const resultMap = new Map(result);
            assert.strictEqual(resultMap.get('VAR1'), 'base1');
            assert.strictEqual(resultMap.get('VAR2'), 'session2');
            assert.strictEqual(resultMap.get('VAR3'), 'base3');
            assert.strictEqual(resultMap.get('VAR4'), 'session4');
        });

        test('merges with run API environment variables taking precedence over base profile', () => {
            const baseProfileEnv = {
                'VAR1': 'base1',
                'VAR2': 'base2',
                'VAR3': 'base3'
            };

            const runApiEnv = {
                'VAR2': 'api2',
                'VAR5': 'api5'
            };

            const runSessionEnv: EnvVar[] = [];

            const result = mergeEnvironmentVariables(baseProfileEnv, undefined, runSessionEnv, runApiEnv);

            assert.strictEqual(result.length, 4);

            const resultMap = new Map(result);
            assert.strictEqual(resultMap.get('VAR1'), 'base1');
            assert.strictEqual(resultMap.get('VAR2'), 'api2');
            assert.strictEqual(resultMap.get('VAR3'), 'base3');
            assert.strictEqual(resultMap.get('VAR5'), 'api5');
        });

        test('run session environment takes precedence over run API environment', () => {
            const baseProfileEnv = {
                'VAR1': 'base1',
                'VAR2': 'base2'
            };

            const runApiEnv = {
                'VAR2': 'api2',
                'VAR3': 'api3'
            };

            const runSessionEnv: EnvVar[] = [
                { name: 'VAR2', value: 'session2' },
                { name: 'VAR4', value: 'session4' }
            ];

            const result = mergeEnvironmentVariables(baseProfileEnv, undefined, runSessionEnv, runApiEnv);

            assert.strictEqual(result.length, 4);

            const resultMap = new Map(result);
            assert.strictEqual(resultMap.get('VAR1'), 'base1');
            assert.strictEqual(resultMap.get('VAR2'), 'session2');
            assert.strictEqual(resultMap.get('VAR3'), 'api3');
            assert.strictEqual(resultMap.get('VAR4'), 'session4');
        });

        test('handles empty base profile environment', () => {
            const runSessionEnv: EnvVar[] = [
                { name: 'VAR1', value: 'session1' }
            ];

            const result = mergeEnvironmentVariables(undefined, undefined, runSessionEnv);

            assert.strictEqual(result.length, 1);
            const resultMap = new Map(result);
            assert.strictEqual(resultMap.get('VAR1'), 'session1');
        });

        test('handles empty run session environment', () => {
            const baseProfileEnv = {
                'VAR1': 'base1',
                'VAR2': 'base2'
            };

            const result = mergeEnvironmentVariables(baseProfileEnv, undefined, []);

            assert.strictEqual(result.length, 2);

            const resultMap = new Map(result);
            assert.strictEqual(resultMap.get('VAR1'), 'base1');
            assert.strictEqual(resultMap.get('VAR2'), 'base2');
        });

        test('handles only run API environment without base or session', () => {
            const runApiEnv = {
                'VAR1': 'api1',
                'VAR2': 'api2'
            };

            const result = mergeEnvironmentVariables(undefined, undefined, [], runApiEnv);

            assert.strictEqual(result.length, 2);

            const resultMap = new Map(result);
            assert.strictEqual(resultMap.get('VAR1'), 'api1');
            assert.strictEqual(resultMap.get('VAR2'), 'api2');
        });

        test('debug configuration environment overrides launch profile environment', () => {
            const launchProfileEnv = {
                'VAR1': 'profile1',
                'VAR2': 'profile2',
                'VAR3': 'profile3'
            };

            const debugConfigEnv = {
                'VAR2': 'debug2',
                'VAR4': 'debug4'
            };

            const runSessionEnv: EnvVar[] = [];

            const result = mergeEnvironmentVariables(launchProfileEnv, debugConfigEnv, runSessionEnv);

            assert.strictEqual(result.length, 4);

            const resultMap = new Map(result);
            assert.strictEqual(resultMap.get('VAR1'), 'profile1');
            assert.strictEqual(resultMap.get('VAR2'), 'debug2');
            assert.strictEqual(resultMap.get('VAR3'), 'profile3');
            assert.strictEqual(resultMap.get('VAR4'), 'debug4');
        });

        test('run API environment overrides debug configuration environment', () => {
            const launchProfileEnv = {
                'VAR1': 'profile1'
            };

            const debugConfigEnv = {
                'VAR2': 'debug2',
                'VAR3': 'debug3'
            };

            const runApiEnv = {
                'VAR3': 'api3',
                'VAR4': 'api4'
            };

            const runSessionEnv: EnvVar[] = [];

            const result = mergeEnvironmentVariables(launchProfileEnv, debugConfigEnv, runSessionEnv, runApiEnv);

            assert.strictEqual(result.length, 4);

            const resultMap = new Map(result);
            assert.strictEqual(resultMap.get('VAR1'), 'profile1');
            assert.strictEqual(resultMap.get('VAR2'), 'debug2');
            assert.strictEqual(resultMap.get('VAR3'), 'api3');
            assert.strictEqual(resultMap.get('VAR4'), 'api4');
        });

        test('run session environment overrides debug configuration environment', () => {
            const launchProfileEnv = {
                'VAR1': 'profile1'
            };

            const debugConfigEnv = {
                'VAR2': 'debug2',
                'VAR3': 'debug3'
            };

            const runSessionEnv: EnvVar[] = [
                { name: 'VAR3', value: 'session3' },
                { name: 'VAR4', value: 'session4' }
            ];

            const result = mergeEnvironmentVariables(launchProfileEnv, debugConfigEnv, runSessionEnv);

            assert.strictEqual(result.length, 4);

            const resultMap = new Map(result);
            assert.strictEqual(resultMap.get('VAR1'), 'profile1');
            assert.strictEqual(resultMap.get('VAR2'), 'debug2');
            assert.strictEqual(resultMap.get('VAR3'), 'session3');
            assert.strictEqual(resultMap.get('VAR4'), 'session4');
        });

        test('handles all four sources with correct precedence: session > api > debugConfig > profile', () => {
            const launchProfileEnv = {
                'PROFILE_ONLY': 'profile_value',
                'OVERRIDDEN_BY_DEBUG': 'profile_value',
                'OVERRIDDEN_BY_API': 'profile_value',
                'OVERRIDDEN_BY_SESSION': 'profile_value',
                'OVERRIDDEN_BY_ALL': 'profile_value'
            };

            const debugConfigEnv = {
                'DEBUG_ONLY': 'debug_value',
                'OVERRIDDEN_BY_DEBUG': 'debug_value',
                'OVERRIDDEN_BY_API': 'debug_value',
                'OVERRIDDEN_BY_SESSION': 'debug_value',
                'OVERRIDDEN_BY_ALL': 'debug_value'
            };

            const runApiEnv = {
                'API_ONLY': 'api_value',
                'OVERRIDDEN_BY_API': 'api_value',
                'OVERRIDDEN_BY_SESSION': 'api_value',
                'OVERRIDDEN_BY_ALL': 'api_value'
            };

            const runSessionEnv: EnvVar[] = [
                { name: 'SESSION_ONLY', value: 'session_value' },
                { name: 'OVERRIDDEN_BY_SESSION', value: 'session_value' },
                { name: 'OVERRIDDEN_BY_ALL', value: 'session_value' }
            ];

            const result = mergeEnvironmentVariables(launchProfileEnv, debugConfigEnv, runSessionEnv, runApiEnv);

            const resultMap = new Map(result);
            assert.strictEqual(resultMap.get('PROFILE_ONLY'), 'profile_value');
            assert.strictEqual(resultMap.get('DEBUG_ONLY'), 'debug_value');
            assert.strictEqual(resultMap.get('API_ONLY'), 'api_value');
            assert.strictEqual(resultMap.get('SESSION_ONLY'), 'session_value');
            assert.strictEqual(resultMap.get('OVERRIDDEN_BY_DEBUG'), 'debug_value');
            assert.strictEqual(resultMap.get('OVERRIDDEN_BY_API'), 'api_value');
            assert.strictEqual(resultMap.get('OVERRIDDEN_BY_SESSION'), 'session_value');
            assert.strictEqual(resultMap.get('OVERRIDDEN_BY_ALL'), 'session_value');
        });

        test('handles only debug configuration environment', () => {
            const debugConfigEnv = {
                'VAR1': 'debug1',
                'VAR2': 'debug2'
            };

            const result = mergeEnvironmentVariables(undefined, debugConfigEnv, []);

            assert.strictEqual(result.length, 2);

            const resultMap = new Map(result);
            assert.strictEqual(resultMap.get('VAR1'), 'debug1');
            assert.strictEqual(resultMap.get('VAR2'), 'debug2');
        });
    });

    suite('determineArguments', () => {
        test('uses run session args when provided', () => {
            const baseProfileArgs = '--base-arg value';
            const runSessionArgs = ['--session-arg', 'value'];

            const result = determineArguments(baseProfileArgs, runSessionArgs);

            assert.deepStrictEqual(result, '--session-arg value');
        });

        test('uses empty run session args when explicitly provided', () => {
            const baseProfileArgs = '--base-arg value';
            const runSessionArgs: string[] = [];

            const result = determineArguments(baseProfileArgs, runSessionArgs);

            assert.deepStrictEqual(result, '');
        });

        test('uses base profile args when run session args are null', () => {
            const baseProfileArgs = '--base-arg value --flag';
            const runSessionArgs = null;

            const result = determineArguments(baseProfileArgs, runSessionArgs);

            assert.deepStrictEqual(result, baseProfileArgs);
        });

        test('uses base profile args when run session args are undefined', () => {
            const baseProfileArgs = '--base-arg value --flag';
            const runSessionArgs = undefined;

            const result = determineArguments(baseProfileArgs, runSessionArgs);

            assert.deepStrictEqual(result, baseProfileArgs);
        });

        test('returns undefined when no args available', () => {
            const result = determineArguments(undefined, undefined);

            assert.deepStrictEqual(result, undefined);
        });
    });

    suite('determineWorkingDirectory', () => {
        const systemRoot = path.parse(process.cwd()).root;
        const projectPath = path.join(systemRoot, 'project', 'MyApp.csproj');
        const projectDir = path.dirname(projectPath);
        const absoluteWorkingDir = path.join(systemRoot, 'custom', 'working', 'dir');
        const toDebugPath = (value: string): string => path.posix.normalize(value.replace(/\\/g, '/'));

        test('uses absolute working directory from launch profile', () => {
            const baseProfile: LaunchProfile = {
                commandName: 'Project',
                workingDirectory: absoluteWorkingDir
            };

            const result = determineWorkingDirectory(projectPath, baseProfile);

            assert.strictEqual(result, toDebugPath(absoluteWorkingDir));
        });

        test('resolves relative working directory from launch profile', () => {
            const baseProfile: LaunchProfile = {
                commandName: 'Project',
                workingDirectory: 'custom'
            };

            const result = determineWorkingDirectory(projectPath, baseProfile);

            assert.strictEqual(result, toDebugPath(path.join(projectDir, 'custom')));
        });

        test('uses project directory when no working directory specified', () => {
            const baseProfile: LaunchProfile = {
                commandName: 'Project'
            };

            const result = determineWorkingDirectory(projectPath, baseProfile);

            assert.strictEqual(result, toDebugPath(projectDir));
        });

        test('uses project directory when base profile is null', () => {
            const result = determineWorkingDirectory(projectPath, null);

            assert.strictEqual(result, toDebugPath(projectDir));
        });

        test('expands environment variables in working directory before resolving', () => {
            process.env['TEST_WD_ROOT'] = '/opt/app';
            const baseProfile: LaunchProfile = {
                commandName: 'Executable',
                workingDirectory: '$(TEST_WD_ROOT)/output'
            };

            const result = determineWorkingDirectory('/dummy/project.csproj', baseProfile);

            assert.strictEqual(result, toDebugPath('/opt/app/output'));
            delete process.env['TEST_WD_ROOT'];
        });

        test('expands environment variables in relative working directory', () => {
            process.env['TEST_WD_SUBDIR'] = 'build-output';
            const baseProfile: LaunchProfile = {
                commandName: 'Executable',
                workingDirectory: '$(TEST_WD_SUBDIR)/bin'
            };

            const result = determineWorkingDirectory('/projects/myapp/myapp.csproj', baseProfile);

            assert.strictEqual(result, toDebugPath(path.resolve('/projects/myapp', 'build-output/bin')));
            delete process.env['TEST_WD_SUBDIR'];
        });

        test('normalizes Windows-style project paths to forward slashes', () => {
            const baseProfile: LaunchProfile = {
                commandName: 'Project',
                workingDirectory: 'custom'
            };

            const result = determineWorkingDirectory('C:\\project\\MyApp.csproj', baseProfile);

            assert.strictEqual(result, 'C:/project/custom');
        });
    });

    suite('expandEnvironmentVariables', () => {
        test('expands $(VAR) syntax from process.env', () => {
            process.env['TEST_EXPAND_VAR'] = '/test/path';
            const result = expandEnvironmentVariables('$(TEST_EXPAND_VAR)/subfolder');
            assert.strictEqual(result, '/test/path/subfolder');
            delete process.env['TEST_EXPAND_VAR'];
        });

        test('expands %VAR% syntax from process.env', () => {
            process.env['TEST_EXPAND_WIN'] = 'C:\\Users\\test';
            const result = expandEnvironmentVariables('%TEST_EXPAND_WIN%\\subfolder');
            assert.strictEqual(result, 'C:\\Users\\test\\subfolder');
            delete process.env['TEST_EXPAND_WIN'];
        });

        test('expands multiple variables in one string', () => {
            process.env['TEST_HOME'] = '/home/user';
            process.env['TEST_VERSION'] = '1.0.0';
            const result = expandEnvironmentVariables('$(TEST_HOME)/.store/tool/$(TEST_VERSION)/content');
            assert.strictEqual(result, '/home/user/.store/tool/1.0.0/content');
            delete process.env['TEST_HOME'];
            delete process.env['TEST_VERSION'];
        });

        test('replaces undefined variables with empty string', () => {
            delete process.env['NONEXISTENT_VAR_12345'];
            const result = expandEnvironmentVariables('prefix/$(NONEXISTENT_VAR_12345)/suffix');
            assert.strictEqual(result, 'prefix//suffix');
        });

        test('returns string unchanged when no variables present', () => {
            const result = expandEnvironmentVariables('/plain/path/no/vars');
            assert.strictEqual(result, '/plain/path/no/vars');
        });

        test('expands HOME variable like AWS Lambda launch profiles use', () => {
            const home = process.env['HOME'] ?? '';
            const input = '$(HOME)/.dotnet/tools/.store/amazon.lambda.testtool/0.13.0/content/RuntimeSupport.dll';
            const result = expandEnvironmentVariables(input);
            assert.strictEqual(result, `${home}/.dotnet/tools/.store/amazon.lambda.testtool/0.13.0/content/RuntimeSupport.dll`);
        });

        test('handles mixed $(VAR) and %VAR% in same string', () => {
            process.env['TEST_MIX_A'] = 'alpha';
            process.env['TEST_MIX_B'] = 'beta';
            const result = expandEnvironmentVariables('$(TEST_MIX_A)/%TEST_MIX_B%/end');
            assert.strictEqual(result, 'alpha/beta/end');
            delete process.env['TEST_MIX_A'];
            delete process.env['TEST_MIX_B'];
        });
    });
});
