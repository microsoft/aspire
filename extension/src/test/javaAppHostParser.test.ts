import * as assert from 'assert';
import * as fs from 'fs/promises';
import * as path from 'path';
import * as vscode from 'vscode';
import { getParserForDocument } from '../editor/parsers/AppHostResourceParser';
import '../editor/parsers/javaAppHostParser';
import { createMockDocument } from './testHelpers';

// Both playground AppHosts are JEP 512 implicitly declared classes, but they differ in ways the
// parser has to absorb: `var` vs explicit types, and `CreateBuilder()` vs `CreateBuilder(args)`.
const implicitClassAppHost = [
    'import aspire.*;',
    '',
    'void main() throws Exception {',
    '    var builder = DistributedApplication.CreateBuilder();',
    '',
    '    var catalog = builder.addSpringBootApp("catalog", "./catalog")',
    '        .withOtelAgentDefaultPath()',
    '        .withExternalHttpEndpoints();',
    '',
    '    builder.addJavaAppWithJar("worker", "./worker", "target/worker.jar");',
    '',
    '    builder.build().run();',
    '}',
].join('\n');

const explicitClassAppHost = [
    'import aspire.*;',
    '',
    'public class AppHost {',
    '    public static void main(String[] args) throws Exception {',
    '        IDistributedApplicationBuilder builder = DistributedApplication.CreateBuilder(args);',
    '        NodeAppResource app = builder.addNodeApp("app", "./api", "src/index.ts");',
    '        builder.build().run();',
    '    }',
    '}',
].join('\n');

function javaDoc(content: string): vscode.TextDocument {
    return createMockDocument(content, '/repo/AppHost.java');
}

// Tests run from out/test, so the repository root is four levels up (out/test -> out -> extension -> repo).
const repositoryRoot = path.resolve(__dirname, '..', '..', '..');

const playgroundAppHostPaths = [
    path.join(repositoryRoot, 'playground', 'JavaAppHost', 'AppHost.java'),
    path.join(repositoryRoot, 'playground', 'JavaSpringBoot', 'JavaSpringBoot.AppHost.Java', 'AppHost.java'),
];

suite('Java AppHost parser', () => {
    test('recognises an implicitly declared AppHost', async () => {
        const parser = await getParserForDocument(javaDoc(implicitClassAppHost));

        assert.ok(parser, 'expected a parser for a Java AppHost');
        assert.deepStrictEqual(parser.getSupportedExtensions(), ['.java']);
    });

    test('recognises a conventional class AppHost, which is the Maven and Gradle project shape', async () => {
        const parser = await getParserForDocument(javaDoc(explicitClassAppHost));

        assert.ok(parser, 'expected a parser for a class-based Java AppHost');
    });

    test('does not claim a Java file that never builds an application', async () => {
        const parser = await getParserForDocument(javaDoc('class Helper {\n    void main() {\n    }\n}'));

        assert.strictEqual(parser, undefined);
    });

    test('recognises a fully qualified CreateBuilder call, which needs no import', async () => {
        // Writing the type out in full is ordinary Java, and the single-file AppHost shape invites it
        // because it avoids the wildcard import entirely. The C# parser already matches on the trailing
        // segment for the same reason.
        const parser = await getParserForDocument(javaDoc([
            'void main(String[] args) throws Exception {',
            '    var builder = aspire.DistributedApplication.CreateBuilder(args);',
            '    builder.addSpringBootApp("catalog", "./catalog");',
            '    builder.build().run();',
            '}',
        ].join('\n')));

        assert.ok(parser, 'expected a parser for a fully qualified Java AppHost');
    });

    test('still refuses an unrelated CreateBuilder on a different type', async () => {
        // Matching the trailing segment must not degrade into matching the method name alone.
        const parser = await getParserForDocument(javaDoc([
            'void main() {',
            '    var builder = ReportBuilder.CreateBuilder();',
            '    builder.addSpringBootApp("catalog", "./catalog");',
            '}',
        ].join('\n')));

        assert.strictEqual(parser, undefined);
    });

    test('extracts every resource with its name, method and anchor line', async () => {
        const document = javaDoc(implicitClassAppHost);
        const parser = await getParserForDocument(document);
        const resources = await parser!.parseResources(document);

        assert.deepStrictEqual(resources.map(r => [r.name, r.methodName, r.kind, r.statementStartLine]), [
            ['catalog', 'addSpringBootApp', 'resource', 5],
            ['worker', 'addJavaAppWithJar', 'resource', 9],
        ]);
    });

    test('anchors a multi-line fluent chain on the declaration, not the chained call', async () => {
        const document = javaDoc(implicitClassAppHost);
        const parser = await getParserForDocument(document);
        const resources = await parser!.parseResources(document);

        assert.strictEqual(resources[0].range.start.line, 5, 'the range starts on the addSpringBootApp line');
        assert.strictEqual(resources[0].statementStartLine, 5, 'and the statement starts on the var declaration');
    });

    test('extracts resources declared with an explicit type', async () => {
        const document = javaDoc(explicitClassAppHost);
        const parser = await getParserForDocument(document);
        const resources = await parser!.parseResources(document);

        assert.deepStrictEqual(resources.map(r => r.name), ['app']);
    });

    test('classifies addStep as a pipeline step', async () => {
        const document = javaDoc('void main() {\n    var builder = DistributedApplication.CreateBuilder();\n    builder.addStep("deploy");\n}');
        const parser = await getParserForDocument(document);
        const resources = await parser!.parseResources(document);

        assert.deepStrictEqual(resources.map(r => [r.name, r.kind]), [['deploy', 'pipelineStep']]);
    });

    test('ignores commented-out and quoted resource calls', async () => {
        const document = javaDoc([
            'void main() {',
            '    var builder = DistributedApplication.CreateBuilder();',
            '    // builder.addRedis("commented");',
            '    /* builder.addPostgres("blocked"); */',
            '    var sample = "builder.addRedis(\\"quoted\\")";',
            '    builder.addRedis("real");',
            '}',
        ].join('\n'));
        const parser = await getParserForDocument(document);
        const resources = await parser!.parseResources(document);

        assert.deepStrictEqual(resources.map(r => r.name), ['real']);
    });

    test('ignores an add call whose first argument is not a literal name', async () => {
        const document = javaDoc('void main() {\n    var builder = DistributedApplication.CreateBuilder();\n    builder.addRedis(nameVariable);\n}');
        const parser = await getParserForDocument(document);
        const resources = await parser!.parseResources(document);

        assert.deepStrictEqual(resources, []);
    });

    test('finds the builder statement line for both AppHost shapes', async () => {
        const implicitDoc = javaDoc(implicitClassAppHost);
        const explicitDoc = javaDoc(explicitClassAppHost);

        assert.strictEqual(await (await getParserForDocument(implicitDoc))!.findBuilderStatementLine!(implicitDoc), 3);
        assert.strictEqual(await (await getParserForDocument(explicitDoc))!.findBuilderStatementLine!(explicitDoc), 4);
    });

    test('finds the entry point line for both AppHost shapes', async () => {
        const implicitDoc = javaDoc(implicitClassAppHost);
        const explicitDoc = javaDoc(explicitClassAppHost);

        assert.strictEqual(await (await getParserForDocument(implicitDoc))!.findAppHostEntryPointLine!(implicitDoc), 2);
        assert.strictEqual(await (await getParserForDocument(explicitDoc))!.findAppHostEntryPointLine!(explicitDoc), 3);
    });

    test('does not let a nested class main override the enclosing AppHost entry point', async () => {
        const document = javaDoc([
            'class AppHost {',
            '    void main() {',
            '        var builder = DistributedApplication.CreateBuilder();',
            '        builder.build().run();',
            '    }',
            '',
            '    static class Helper {',
            '        public static void main(String[] args) {',
            '        }',
            '    }',
            '}',
        ].join('\n'));
        const parser = await getParserForDocument(document);

        assert.strictEqual(await parser!.findAppHostEntryPointLine!(document), 1);
    });

    test('skips main-like methods that are not Java entry points', async () => {
        const cases = [
            {
                name: 'unsupported parameter before an instance main',
                content: [
                    'void main(int value) {',
                    '}',
                    '',
                    'void main() throws Exception {',
                    '    var builder = DistributedApplication.CreateBuilder();',
                    '    builder.build().run();',
                    '}',
                ].join('\n'),
                expectedLine: 3,
            },
            {
                name: 'non-void method before a static main',
                content: [
                    'class AppHost {',
                    '    int main() { return 0; }',
                    '',
                    '    public static void main(String[] args) throws Exception {',
                    '        var builder = DistributedApplication.CreateBuilder(args);',
                    '        builder.build().run();',
                    '    }',
                    '}',
                ].join('\n'),
                expectedLine: 3,
            },
            {
                name: 'private method before a static main',
                content: [
                    'class AppHost {',
                    '    private void main() {',
                    '    }',
                    '',
                    '    public static void main(String[] args) throws Exception {',
                    '        var builder = DistributedApplication.CreateBuilder(args);',
                    '        builder.build().run();',
                    '    }',
                    '}',
                ].join('\n'),
                expectedLine: 4,
            },
        ];

        for (const testCase of cases) {
            const document = javaDoc(testCase.content);
            const parser = await getParserForDocument(document);

            assert.strictEqual(
                await parser!.findAppHostEntryPointLine!(document),
                testCase.expectedLine,
                testCase.name);
        }
    });

    // The tests above parse hand-written approximations of the playground AppHosts. Those stay
    // readable, but they drift: they do not exercise array initializers, `new Options().path(...)`
    // arguments, or comments interleaved through a builder chain. When the real files stop being
    // recognised, every editor feature keyed off the parser disappears silently — the entry point
    // warning, the resource lenses, and the dashboard links — because a missing parser is
    // indistinguishable from an ordinary Java file. So parse the files we actually ship.
    for (const appHostPath of playgroundAppHostPaths) {
        test(`recognises the ${path.basename(path.dirname(appHostPath))} playground AppHost`, async () => {
            const document = createMockDocument(await fs.readFile(appHostPath, 'utf8'), appHostPath);
            const parser = await getParserForDocument(document);

            assert.ok(parser, `expected ${appHostPath} to be recognised as a Java AppHost`);
            assert.notStrictEqual(
                await parser.findBuilderStatementLine!(document),
                undefined,
                'the builder statement anchors the dashboard and log lenses');
            assert.notStrictEqual(
                await parser.findAppHostEntryPointLine!(document),
                undefined,
                'the entry point anchors the warning that the Java Run and Debug actions bypass Aspire');
        });
    }

    test('filters offsets that fall inside comments and strings', async () => {
        const content = 'void main() {\n    var builder = DistributedApplication.CreateBuilder();\n    // addRedis\n    builder.addRedis("real");\n}';
        const document = javaDoc(content);
        const parser = await getParserForDocument(document);

        const commentOffset = content.indexOf('addRedis');
        const codeOffset = content.indexOf('addRedis("real")');

        assert.deepStrictEqual(await parser!.filterActiveOffsets!(document, [commentOffset, codeOffset]), [codeOffset]);
    });
});
