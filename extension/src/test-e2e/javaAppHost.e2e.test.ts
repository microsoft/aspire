import * as assert from 'assert';
import * as fs from 'fs';
import * as path from 'path';
import { executeE2eControlCommand } from './helpers/fixtures';
import { JAVA_APP_HOST_DIRECTORY, getJavaAppHostSourcePath, prepareJavaWorkspace, waitForJavaLanguageServerImport } from './helpers/java';
import { getWorkspaceRoot } from './helpers/paths';

interface DiagnosticInfo {
    message: string;
    severity: number;
    code?: string | number;
}

interface BreakpointProofStackFrame {
    line?: number;
    name?: string;
    source?: { path?: string };
}

interface BreakpointProof {
    sourcePath: string;
    line: number;
    text?: string;
    matchingStackFrame: BreakpointProofStackFrame;
    topStackFrame?: BreakpointProofStackFrame;
}

interface DebugProof {
    proof: string;
    appHostBreakpoint: BreakpointProof;
    resourceBreakpoint: BreakpointProof;
    debugSessions: { type: string; name: string }[];
    launchRequests: { sessionType: string; arguments?: Record<string, unknown> }[];
}

// VS Code numbers DiagnosticSeverity from most to least severe, so Error is 0 and Warning is 1.
const DIAGNOSTIC_SEVERITY_ERROR = 0;
const DIAGNOSTIC_SEVERITY_WARNING = 1;

const APP_HOST_DIRECTORY = JAVA_APP_HOST_DIRECTORY;
const CATALOG_CONTROLLER_SOURCE = path.join('catalog', 'src', 'main', 'java', 'com', 'example', 'catalog', 'CatalogController.java');

suite('Aspire Java AppHost E2E', function () {
    // A cold Gradle import compiles the Spring Boot services and downloads a Java 21 toolchain, and
    // the debug proof then runs the AppHost end to end on top of that.
    this.timeout(1800000);

    // Importing the workspace is what the first two tests are about, so it is shared setup rather
    // than the side effect of whichever test happens to run first. Without it, running either test
    // on its own measured an empty workspace: the language server had produced neither diagnostics
    // nor output directories, and both assertions hold trivially over nothing.
    suiteSetup(async () => {
        await prepareJavaWorkspace();
        await executeE2eControlCommand({ name: 'openFile', filePath: getJavaAppHostSourcePath() });
        await waitForJavaLanguageServerImport();
    });

    test('imports the workspace without reporting problems in generated or authored sources', async () => {
        const appHostSourcePath = getJavaAppHostSourcePath();

        const generatedSources = findGeneratedSdkSources();
        assert.ok(
            generatedSources.length > 100,
            `Expected the generated Aspire Java SDK under ${APP_HOST_DIRECTORY}/.aspire/modules. Found ${generatedSources.length} files.`);

        const offenders: string[] = [];
        for (const sourcePath of [appHostSourcePath, ...generatedSources]) {
            const diagnostics = (await executeE2eControlCommand({ name: 'getDiagnostics', filePath: sourcePath })).result as DiagnosticInfo[];
            const problems = diagnostics.filter(diagnostic =>
                diagnostic.severity === DIAGNOSTIC_SEVERITY_ERROR || diagnostic.severity === DIAGNOSTIC_SEVERITY_WARNING);

            for (const problem of problems) {
                offenders.push(`${path.relative(getWorkspaceRoot(), sourcePath)}: [${problem.severity === DIAGNOSTIC_SEVERITY_ERROR ? 'error' : 'warning'}] ${problem.message}`);
            }
        }

        assert.deepStrictEqual(
            offenders,
            [],
            `Opening a Java AppHost must not report problems the user cannot act on. Checked ${generatedSources.length + 1} files.`);
    });

    test('does not copy build inputs into the language server output directory', () => {
        // Rooting the java source set at '.' also points the resources source set there unless the
        // build file says otherwise, and processResources then copies .gradle/, .aspire/ and the
        // wrapper into build output. The language server digests those copies and fails to refresh
        // the workspace once Gradle deletes them.
        const appHostDirectory = path.join(getWorkspaceRoot(), APP_HOST_DIRECTORY);

        // Nothing below can fail while the project has no output at all, so require the output
        // directory first. The workspace is copied in without one, which leaves the language
        // server's own compile as the only thing that can produce it.
        const outputDirectory = path.join(appHostDirectory, 'bin', 'main');
        assert.ok(
            fs.existsSync(outputDirectory),
            `The language server produced no output under ${path.relative(getWorkspaceRoot(), outputDirectory)}, so this test cannot observe what a build copies there. ${APP_HOST_DIRECTORY} holds: ${fs.readdirSync(appHostDirectory).join(', ')}`);

        const copied: string[] = [];
        // build/ only appears when Gradle itself builds the project, which no test here does; it is
        // listed because the same source set misconfiguration fills both directories, and a run that
        // does build should fail on it too.
        for (const candidate of ['build/resources', 'bin/main/.gradle', 'bin/main/.aspire', 'bin/main/gradlew', 'bin/main/build.gradle', 'bin/main/aspire.config.json']) {
            if (fs.existsSync(path.join(appHostDirectory, candidate))) {
                copied.push(candidate);
            }
        }

        assert.deepStrictEqual(copied, [], `Build inputs were copied into the AppHost's output directories: ${copied.join(', ')}.`);
    });

    test('stops on breakpoints in both the Java AppHost and a Java resource', async () => {
        const appHostSourcePath = getJavaAppHostSourcePath();
        const resourceSourcePath = path.join(getWorkspaceRoot(), CATALOG_CONTROLLER_SOURCE);

        const proof = (await executeE2eControlCommand({
            name: 'proveAppHostAndResourceDebugging',
            appHostPath: appHostSourcePath,
            resourceName: 'catalog',
            appHostSourcePath,
            appHostBreakpointLine: findBreakpointLine(appHostSourcePath, 'builder.addSpringBootApp("catalog"'),
            resourceSourcePath,
            resourceBreakpointLine: findBreakpointLine(resourceSourcePath, 'return Products;'),
            // The breakpoint sits in the /products handler, so that is the request the proof has to
            // send for the line to run at all.
            resourceRequestPath: '/products',
            timeoutMs: 900000,
        }, {
            // The command runs the whole AppHost, so the harness has to wait longer than the default
            // 10s for the control file to be marked applied - otherwise the wait fails while the
            // proof is still legitimately running.
            timeoutMs: 960000,
        })).result as DebugProof;

        assert.strictEqual(proof.proof, 'aspire-apphost-and-resource-debug-breakpoints-hit');

        // The stack frame is what makes this a proof rather than an assertion that a session started:
        // it can only name these files if the adapter actually suspended there with source resolved.
        assert.ok(
            proof.appHostBreakpoint.matchingStackFrame.source?.path,
            `The AppHost breakpoint hit did not resolve a source path: ${JSON.stringify(proof.appHostBreakpoint)}`);
        assert.ok(
            proof.resourceBreakpoint.matchingStackFrame.source?.path,
            `The resource breakpoint hit did not resolve a source path: ${JSON.stringify(proof.resourceBreakpoint)}`);

        // Aspire delegates Java resources to vscjava.vscode-java-debug rather than attaching over JDWP
        // itself, so a session of any other type would mean the delegation regressed.
        const javaSessions = proof.debugSessions.filter(session => session.type === 'java');
        assert.ok(
            javaSessions.length > 0,
            `Expected at least one 'java' debug session. Saw: ${JSON.stringify(proof.debugSessions)}`);
    });
});

/**
 * Finds the zero-based line of the first occurrence of a marker.
 *
 * Hard-coding line numbers makes the sample impossible to edit without silently moving the
 * breakpoint onto an unrelated statement, which the proof would then report as a timeout.
 */
function findBreakpointLine(sourcePath: string, marker: string): number {
    const lines = fs.readFileSync(sourcePath, 'utf8').split(/\r?\n/);
    const index = lines.findIndex(line => line.includes(marker));
    if (index < 0) {
        throw new Error(`Could not find '${marker}' in ${sourcePath} to place a breakpoint on.`);
    }

    return index;
}

function findGeneratedSdkSources(): string[] {
    const modulesRoot = path.join(getWorkspaceRoot(), APP_HOST_DIRECTORY, '.aspire', 'modules');
    if (!fs.existsSync(modulesRoot)) {
        return [];
    }

    return fs.readdirSync(modulesRoot, { recursive: true, withFileTypes: true })
        .filter(entry => entry.isFile() && entry.name.endsWith('.java'))
        .map(entry => path.join(entry.parentPath ?? modulesRoot, entry.name));
}
