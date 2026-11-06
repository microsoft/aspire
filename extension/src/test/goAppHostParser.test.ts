import * as assert from 'assert';
import { getParserForDocument, getAllParsers } from '../editor/parsers/AppHostResourceParser';
import { createMockDocument } from './testHelpers';
import '../editor/parsers/goAppHostParser';

suite('GoAppHostParser', () => {
    const parser = getAllParsers().find(candidate => candidate.getSupportedExtensions().includes('.go'))!;
    const document = (text: string) => createMockDocument(text, '/test/apphost.go');

    test('registers Go AppHosts and ignores commented and quoted builder examples', async () => {
        assert.strictEqual(await getParserForDocument(document('builder, err := sdk.CreateBuilder()')), parser);
        for (const text of ['// aspire.CreateBuilder()', '/* aspire.CreateBuilder() */', '`aspire.CreateBuilder()`', '"aspire.CreateBuilder()"', 'func main() {}']) {
            assert.strictEqual(await getParserForDocument(document(text)), undefined, text);
        }
    });

    test('parses resource and pipeline calls with comments, multiline arguments and chains', async () => {
        const doc = document([
            'package main',
            'func main() {',
            '    builder, err := aspire.CreateBuilder()',
            '    db := builder.AddPostgres("postgres").AddDatabase(`db`)',
            '    api := builder.',
            '        AddProject(/* name */ "api", "../api")',
            '    builder.AddStep(',
            '        "deploy", action)',
            '}',
        ].join('\n'));
        const resources = await parser.parseResources(doc);
        assert.deepStrictEqual(resources.map(resource => [resource.name, resource.methodName, resource.kind]), [
            ['postgres', 'AddPostgres', 'resource'],
            ['db', 'AddDatabase', 'resource'],
            ['api', 'AddProject', 'resource'],
            ['deploy', 'AddStep', 'pipelineStep'],
        ]);
        assert.strictEqual(doc.getText(resources[0].range), '.AddPostgres("postgres"');
        assert.strictEqual(resources[2].statementStartLine, 4);
        assert.strictEqual(await parser.findBuilderStatementLine!(doc), 2);
        assert.strictEqual(await parser.findAppHostEntryPointLine!(doc), 1);
    });

    test('does not interpret examples or computed names as resources', async () => {
        const doc = document([
            '// builder.AddRedis("comment")',
            '/* builder.AddRedis("block") */',
            '`builder.AddRedis("raw")`',
            '"builder.AddRedis(\\"quoted\\")"',
            'builder.AddRedis(name)',
            'builder.AddRedis("prefix" + suffix)',
            'AddRedis("helper")',
            'builder.WithName("renamed")',
        ].join('\n'));
        assert.deepStrictEqual(await parser.parseResources(doc), []);
    });

    test('anchors nested resource expressions to their declaration instead of the package', async () => {
        const doc = document([
            'package main',
            'func main() {',
            '    builder, err := aspire.CreateBuilder()',
            '    cache := (builder.AddRedis("cache"))',
            '    resources = append(resources, builder.AddRedis("nested"))',
            '    resource := builders[lookup()].AddRedis("indexed")',
            '}',
        ].join('\n'));

        assert.deepStrictEqual((await parser.parseResources(doc)).map(resource => ({
            name: resource.name,
            line: resource.statementStartLine,
        })), [
            { name: 'cache', line: 3 },
            { name: 'nested', line: 4 },
            { name: 'indexed', line: 5 },
        ]);
    });

    test('decodes Go strings exactly and rejects invalid escapes', async () => {
        const doc = document(String.raw`
builder.AddRedis("\x63\u0061\U00000063\150e")
builder.AddRedis("\q")
builder.AddRedis("\uD800")
builder.AddRedis("\400")
builder.AddRedis("\xc3\xa9")
builder.AddRedis("unterminated)
builder.AddRedis("valid")
`);
        assert.deepStrictEqual((await parser.parseResources(doc)).map(resource => resource.name), ['cache', '\u00e9', 'valid']);
    });

    test('finds split builder declarations and filters inactive offsets', async () => {
        const doc = document('func main() {\n builder, err :=\n aspire.CreateBuilder()\n // WithMavenGoal\n builder.WithMavenGoal("example")\n}');
        assert.strictEqual(await parser.findBuilderStatementLine!(doc), 1);
        const offsets = [...doc.getText().matchAll(/WithMavenGoal/g)].map(match => match.index);
        assert.deepStrictEqual(await parser.filterActiveOffsets!(doc, offsets), [offsets[1]]);
    });
});
