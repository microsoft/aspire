import * as assert from 'assert';
import * as path from 'path';
import * as fs from 'fs';
import * as os from 'os';
import {
    determineBaseLaunchProfile,
    mergeEnvironmentVariables,
    determineArguments,
    determineWorkingDirectory,
    determineServerReadyAction,
    readLaunchSettings,
    expandEnvironmentVariables,
    LaunchSettings,
    LaunchProfile
} from '../debugger/launchProfiles';
import { ExecutableLaunchConfiguration, EnvVar, ProjectLaunchConfiguration } from '../dcp/types';

suite('Launch Profile Tests', () => {
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
            assert.strictEqual(resultMap.get('VAR2'), 'session2'); // Run session takes precedence
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
            assert.strictEqual(resultMap.get('VAR2'), 'api2'); // Run API takes precedence over base
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
            assert.strictEqual(resultMap.get('VAR2'), 'session2'); // Run session has highest precedence
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
            assert.strictEqual(resultMap.get('VAR2'), 'debug2'); // Debug config overrides profile
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
            assert.strictEqual(resultMap.get('VAR3'), 'api3'); // Run API overrides debug config
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
            assert.strictEqual(resultMap.get('VAR3'), 'session3'); // Run session overrides debug config
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
        const projectPath = path.join('C:', 'project', 'MyApp.csproj');

        test('uses absolute working directory from launch profile', () => {
            const baseProfile: LaunchProfile = {
                commandName: 'Project',
                workingDirectory: path.join('C:', 'custom', 'working', 'dir')
            };

            const result = determineWorkingDirectory(projectPath, baseProfile);

            assert.strictEqual(result, path.join('C:', 'custom', 'working', 'dir'));
        });

        test('resolves relative working directory from launch profile', () => {
            const baseProfile: LaunchProfile = {
                commandName: 'Project',
                workingDirectory: 'custom'
            };

            const result = determineWorkingDirectory(projectPath, baseProfile);

            assert.strictEqual(result, path.join('C:', 'project', 'custom'));
        });

        test('uses project directory when no working directory specified', () => {
            const baseProfile: LaunchProfile = {
                commandName: 'Project'
            };

            const result = determineWorkingDirectory(projectPath, baseProfile);

            assert.strictEqual(result, path.join('C:', 'project'));
        });

        test('uses project directory when base profile is null', () => {
            const result = determineWorkingDirectory(projectPath, null);

            assert.strictEqual(result, path.join('C:', 'project'));
        });

        test('expands environment variables in working directory before resolving', () => {
            process.env['TEST_WD_ROOT'] = '/opt/app';
            const baseProfile: LaunchProfile = {
                commandName: 'Executable',
                workingDirectory: '$(TEST_WD_ROOT)/output'
            };

            const result = determineWorkingDirectory('/dummy/project.csproj', baseProfile);

            // $(TEST_WD_ROOT) expands to /opt/app, making it an absolute path
            assert.strictEqual(result, '/opt/app/output');
            delete process.env['TEST_WD_ROOT'];
        });

        test('expands environment variables in relative working directory', () => {
            process.env['TEST_WD_SUBDIR'] = 'build-output';
            const baseProfile: LaunchProfile = {
                commandName: 'Executable',
                workingDirectory: '$(TEST_WD_SUBDIR)/bin'
            };

            const result = determineWorkingDirectory('/projects/myapp/myapp.csproj', baseProfile);

            // $(TEST_WD_SUBDIR) expands to build-output, still relative, resolved against project dir
            assert.strictEqual(result, path.resolve('/projects/myapp', 'build-output/bin'));
            delete process.env['TEST_WD_SUBDIR'];
        });
    });

    suite('determineServerReadyAction', () => {
        test('returns undefined when launchBrowser is false', () => {
            const result = determineServerReadyAction(false, 'https://localhost:5001');
            assert.strictEqual(result, undefined);
        });

        test('returns undefined when applicationUrl is undefined', () => {
            const result = determineServerReadyAction(true, undefined);
            assert.strictEqual(result, undefined);
        });

        test('returns serverReadyAction when launchBrowser true and applicationUrl provided', () => {
            const applicationUrl = 'https://localhost:5001';
            const result = determineServerReadyAction(true, applicationUrl);

            assert.notStrictEqual(result, undefined);
            assert.strictEqual(result?.action, 'openExternally');
            assert.strictEqual(result?.uriFormat, applicationUrl);
            assert.strictEqual(result?.pattern, '\\bNow listening on:\\s+https?://\\S+');
        });

        test('returns serverReadyAction with first URL when multiple URLs separated by semicolon', () => {
            const applicationUrl = 'https://localhost:5001;http://localhost:5000';
            const result = determineServerReadyAction(true, applicationUrl);

            assert.notStrictEqual(result, undefined);
            assert.strictEqual(result?.action, 'openExternally');
            assert.strictEqual(result?.uriFormat, 'https://localhost:5001');
            assert.strictEqual(result?.pattern, '\\bNow listening on:\\s+https?://\\S+');
        });
    });

    suite('readLaunchSettings', () => {
        let tempDir: string;
        let projectPath: string;
        let launchSettingsPath: string;

        setup(() => {
            tempDir = fs.mkdtempSync(path.join(os.tmpdir(), 'aspire-test-'));
            const projectDir = path.join(tempDir, 'TestProject');
            const propertiesDir = path.join(projectDir, 'Properties');

            fs.mkdirSync(projectDir, { recursive: true });
            fs.mkdirSync(propertiesDir, { recursive: true });

            projectPath = path.join(projectDir, 'TestProject.csproj');
            launchSettingsPath = path.join(propertiesDir, 'launchSettings.json');

            // Create a dummy project file
            fs.writeFileSync(projectPath, '<Project></Project>');
        });

        teardown(() => {
            if (fs.existsSync(tempDir)) {
                fs.rmSync(tempDir, { recursive: true, force: true });
            }
        });

        test('successfully reads valid launch settings file', async () => {
            const launchSettings = {
                profiles: {
                    'Development': {
                        environmentVariables: {
                            ASPNETCORE_ENVIRONMENT: 'Development'
                        }
                    }
                }
            };

            fs.writeFileSync(launchSettingsPath, JSON.stringify(launchSettings, null, 2));

            const result = await readLaunchSettings(projectPath);

            assert.notStrictEqual(result, null);
            assert.strictEqual(result!.profiles['Development'].environmentVariables!.ASPNETCORE_ENVIRONMENT, 'Development');
        });

        test('returns null when launch settings file does not exist', async () => {
            const result = await readLaunchSettings(projectPath);

            assert.strictEqual(result, null);
        });

        test('returns null when launch settings file has invalid JSON', async () => {
            fs.writeFileSync(launchSettingsPath, '{ invalid json content');

            const result = await readLaunchSettings(projectPath);

            assert.strictEqual(result, null);
        });

        test('handles empty launch settings file', async () => {
            const launchSettings = {
                profiles: {}
            };

            fs.writeFileSync(launchSettingsPath, JSON.stringify(launchSettings));

            const result = await readLaunchSettings(projectPath);

            assert.notStrictEqual(result, null);
            assert.deepStrictEqual(result!.profiles, {});
        });

        test('successfully reads launch settings file with comments', async () => {
            const launchSettingsWithComments = `{
  // This is a comment
  "profiles": {
    /* Multi-line comment
       spanning multiple lines */
    "Development": {
      "commandName": "Project",
      // Comment before environmentVariables
      "environmentVariables": {
        "ASPNETCORE_ENVIRONMENT": "Development", // Inline comment
        "LOG_LEVEL": "Debug" /* Another inline comment */
      },
      // Comment before applicationUrl
      "applicationUrl": "https://localhost:5001",
      "launchBrowser": true
    },
    // Another profile
    "Production": {
      "commandName": "Project",
      "environmentVariables": {
        "ASPNETCORE_ENVIRONMENT": "Production"
      }
    }
  }
}`;

            fs.writeFileSync(launchSettingsPath, launchSettingsWithComments);

            const result = await readLaunchSettings(projectPath);

            assert.notStrictEqual(result, null);
            assert.strictEqual(result!.profiles['Development'].commandName, 'Project');
            assert.strictEqual(result!.profiles['Development'].environmentVariables!.ASPNETCORE_ENVIRONMENT, 'Development');
            assert.strictEqual(result!.profiles['Development'].environmentVariables!.LOG_LEVEL, 'Debug');
            assert.strictEqual(result!.profiles['Development'].applicationUrl, 'https://localhost:5001');
            assert.strictEqual(result!.profiles['Development'].launchBrowser, true);
            assert.strictEqual(result!.profiles['Production'].environmentVariables!.ASPNETCORE_ENVIRONMENT, 'Production');
        });

        test('falls back to aspire.config.json profiles when .run.json does not exist for file-based app', async () => {
            // Create a file-based app (.cs file) with no .run.json
            const fileBasedAppPath = path.join(tempDir, 'TestProject', 'apphost.cs');
            fs.writeFileSync(fileBasedAppPath, '// test file-based app');

            // Create aspire.config.json with profiles
            const aspireConfigPath = path.join(tempDir, 'TestProject', 'aspire.config.json');
            const aspireConfig = {
                appHost: { path: 'apphost.cs' },
                profiles: {
                    https: {
                        applicationUrl: 'https://localhost:5001;http://localhost:5000',
                        environmentVariables: {
                            ASPNETCORE_ENVIRONMENT: 'Development'
                        }
                    },
                    http: {
                        applicationUrl: 'http://localhost:5000'
                    }
                }
            };
            fs.writeFileSync(aspireConfigPath, JSON.stringify(aspireConfig, null, 2));

            const result = await readLaunchSettings(fileBasedAppPath);

            assert.notStrictEqual(result, null);
            assert.strictEqual(Object.keys(result!.profiles).length, 2);
            assert.strictEqual(result!.profiles['https'].applicationUrl, 'https://localhost:5001;http://localhost:5000');
            assert.strictEqual(result!.profiles['https'].environmentVariables!.ASPNETCORE_ENVIRONMENT, 'Development');
            assert.strictEqual(result!.profiles['https'].commandName, 'Project');
            assert.strictEqual(result!.profiles['http'].applicationUrl, 'http://localhost:5000');
        });

        test('returns null when neither .run.json nor aspire.config.json exists for file-based app', async () => {
            const fileBasedAppPath = path.join(tempDir, 'TestProject', 'apphost.cs');
            fs.writeFileSync(fileBasedAppPath, '// test file-based app');

            const result = await readLaunchSettings(fileBasedAppPath);

            assert.strictEqual(result, null);
        });

        test('prefers .run.json over aspire.config.json profiles for file-based app', async () => {
            const fileBasedAppPath = path.join(tempDir, 'TestProject', 'apphost.cs');
            fs.writeFileSync(fileBasedAppPath, '// test file-based app');

            // Create both .run.json and aspire.config.json
            const runJsonPath = path.join(tempDir, 'TestProject', 'apphost.run.json');
            const runJson = {
                profiles: {
                    default: {
                        commandName: 'Project',
                        applicationUrl: 'https://localhost:7000'
                    }
                }
            };
            fs.writeFileSync(runJsonPath, JSON.stringify(runJson, null, 2));

            const aspireConfigPath = path.join(tempDir, 'TestProject', 'aspire.config.json');
            const aspireConfig = {
                profiles: {
                    default: {
                        applicationUrl: 'https://localhost:9999'
                    }
                }
            };
            fs.writeFileSync(aspireConfigPath, JSON.stringify(aspireConfig, null, 2));

            const result = await readLaunchSettings(fileBasedAppPath);

            assert.notStrictEqual(result, null);
            // Should use the .run.json value, not aspire.config.json
            assert.strictEqual(result!.profiles['default'].applicationUrl, 'https://localhost:7000');
        });

        test('reads aspire.config.json profiles with comments', async () => {
            const fileBasedAppPath = path.join(tempDir, 'TestProject', 'apphost.cs');
            fs.writeFileSync(fileBasedAppPath, '// test file-based app');

            const aspireConfigPath = path.join(tempDir, 'TestProject', 'aspire.config.json');
            const aspireConfigWithComments = `{
  // AppHost configuration
  "appHost": { "path": "apphost.cs" },
  "profiles": {
    "https": {
      "applicationUrl": "https://localhost:5001", // HTTPS endpoint
      "environmentVariables": {
        "ASPNETCORE_ENVIRONMENT": "Development"
      }
    }
  }
}`;
            fs.writeFileSync(aspireConfigPath, aspireConfigWithComments);

            const result = await readLaunchSettings(fileBasedAppPath);

            assert.notStrictEqual(result, null);
            assert.strictEqual(result!.profiles['https'].applicationUrl, 'https://localhost:5001');
            assert.strictEqual(result!.profiles['https'].environmentVariables!.ASPNETCORE_ENVIRONMENT, 'Development');
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
