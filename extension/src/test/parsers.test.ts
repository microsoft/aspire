import * as assert from 'assert';
import * as vscode from 'vscode';
import { getParserForDocument, getSupportedLanguageIds, getAllParsers, ParsedResource } from '../editor/parsers/AppHostResourceParser';
// Import parsers so they self-register
import '../editor/parsers/csharpAppHostParser';
import '../editor/parsers/jsTsAppHostParser';

/**
 * Creates a minimal mock TextDocument for parser testing.
 */
function createMockDocument(content: string, filePath: string): vscode.TextDocument {
    const lines = content.split('\n');
    return {
        uri: vscode.Uri.file(filePath),
        fileName: filePath,
        isUntitled: false,
        languageId: filePath.endsWith('.cs') ? 'csharp' : filePath.endsWith('.ts') ? 'typescript' : 'javascript',
        version: 1,
        isDirty: false,
        isClosed: false,
        eol: vscode.EndOfLine.LF,
        lineCount: lines.length,
        encoding: 'utf-8',
        save: () => Promise.resolve(false),
        lineAt: (lineOrPos: number | vscode.Position) => {
            const lineNum = typeof lineOrPos === 'number' ? lineOrPos : lineOrPos.line;
            const text = lines[lineNum] || '';
            return {
                lineNumber: lineNum,
                text,
                range: new vscode.Range(lineNum, 0, lineNum, text.length),
                rangeIncludingLineBreak: new vscode.Range(lineNum, 0, lineNum + 1, 0),
                firstNonWhitespaceCharacterIndex: text.search(/\S/),
                isEmptyOrWhitespace: text.trim().length === 0,
            } as vscode.TextLine;
        },
        offsetAt: (position: vscode.Position) => {
            let offset = 0;
            for (let i = 0; i < position.line && i < lines.length; i++) {
                offset += lines[i].length + 1; // +1 for \n
            }
            offset += position.character;
            return offset;
        },
        positionAt: (offset: number) => {
            let remaining = offset;
            for (let i = 0; i < lines.length; i++) {
                if (remaining <= lines[i].length) {
                    return new vscode.Position(i, remaining);
                }
                remaining -= lines[i].length + 1; // +1 for \n
            }
            return new vscode.Position(lines.length - 1, lines[lines.length - 1].length);
        },
        getText: (range?: vscode.Range) => {
            if (!range) {
                return content;
            }
            const startOffset = lines.slice(0, range.start.line).reduce((sum, l) => sum + l.length + 1, 0) + range.start.character;
            const endOffset = lines.slice(0, range.end.line).reduce((sum, l) => sum + l.length + 1, 0) + range.end.character;
            return content.substring(startOffset, endOffset);
        },
        getWordRangeAtPosition: () => undefined,
        validateRange: (range: vscode.Range) => range,
        validatePosition: (position: vscode.Position) => position,
        notebook: undefined as any,
    } as vscode.TextDocument;
}

// ============================================================
// Parser Registry Tests
// ============================================================
suite('AppHostResourceParser registry', () => {
    test('getAllParsers returns registered parsers', () => {
        const parsers = getAllParsers();
        assert.ok(parsers.length >= 2, 'Should have at least the C# and JS/TS parsers');
    });

    test('getSupportedLanguageIds returns expected languages', () => {
        const ids = getSupportedLanguageIds();
        assert.ok(ids.includes('csharp'), 'Should support csharp');
        assert.ok(ids.includes('typescript'), 'Should support typescript');
        assert.ok(ids.includes('javascript'), 'Should support javascript');
    });

    test('getParserForDocument returns C# parser for .cs AppHost file', () => {
        const doc = createMockDocument(
            'var builder = DistributedApplication.CreateBuilder(args);\nbuilder.AddRedis("cache");',
            '/test/AppHost.cs'
        );
        const parser = getParserForDocument(doc);
        assert.ok(parser, 'Should find a parser');
        assert.ok(parser!.getSupportedExtensions().includes('.cs'));
    });

    test('getParserForDocument returns JS/TS parser for .ts AppHost file', () => {
        const doc = createMockDocument(
            'import { createBuilder } from "@aspire/sdk";\nawait builder.addRedis("cache");',
            '/test/apphost.ts'
        );
        const parser = getParserForDocument(doc);
        assert.ok(parser, 'Should find a parser');
        assert.ok(parser!.getSupportedExtensions().includes('.ts'));
    });

    test('getParserForDocument returns undefined for non-AppHost .cs file', () => {
        const doc = createMockDocument(
            'using System;\nclass Program { static void Main() { } }',
            '/test/Program.cs'
        );
        const parser = getParserForDocument(doc);
        assert.strictEqual(parser, undefined, 'Should not find a parser for non-AppHost C# file');
    });

    test('getParserForDocument returns undefined for non-AppHost .ts file', () => {
        const doc = createMockDocument(
            'import express from "express";\nconst app = express();',
            '/test/server.ts'
        );
        const parser = getParserForDocument(doc);
        assert.strictEqual(parser, undefined, 'Should not find a parser for non-AppHost TS file');
    });

    test('getParserForDocument returns undefined for unsupported extension', () => {
        const doc = createMockDocument(
            'DistributedApplication.CreateBuilder(args);',
            '/test/file.py'
        );
        const parser = getParserForDocument(doc);
        assert.strictEqual(parser, undefined, 'Should not find a parser for .py file');
    });

    test('getParserForDocument returns JS/TS parser for .js AppHost file', () => {
        const doc = createMockDocument(
            'const { createBuilder } = require("@aspire/sdk");\nbuilder.addRedis("cache");',
            '/test/apphost.js'
        );
        const parser = getParserForDocument(doc);
        assert.ok(parser, 'Should find a parser for .js file');
        assert.ok(parser!.getSupportedExtensions().includes('.js'));
    });
});

// ============================================================
// C# Parser Tests
// ============================================================
suite('CSharpAppHostParser', () => {
    function getCSharpParser() {
        return getAllParsers().find(p => p.getSupportedExtensions().includes('.cs'))!;
    }

    // --- isAppHostFile ---

    test('detects AppHost via #:sdk directive', () => {
        const parser = getCSharpParser();
        const doc = createMockDocument(
            '#:sdk Aspire.AppHost.Sdk\n\nvar builder = Aspire.Hosting.DistributedApplication.CreateBuilder(args);',
            '/test/AppHost.cs'
        );
        assert.strictEqual(parser.isAppHostFile(doc), true);
    });

    test('detects AppHost via DistributedApplication.CreateBuilder', () => {
        const parser = getCSharpParser();
        const doc = createMockDocument(
            'using Aspire;\nvar builder = DistributedApplication.CreateBuilder(args);',
            '/test/Program.cs'
        );
        assert.strictEqual(parser.isAppHostFile(doc), true);
    });

    test('rejects non-AppHost C# file', () => {
        const parser = getCSharpParser();
        const doc = createMockDocument(
            'using System;\nclass Foo { void Bar() { } }',
            '/test/Foo.cs'
        );
        assert.strictEqual(parser.isAppHostFile(doc), false);
    });

    // --- parseResources: basic patterns ---

    test('parses single AddRedis call', () => {
        const parser = getCSharpParser();
        const doc = createMockDocument(
            'var builder = DistributedApplication.CreateBuilder(args);\nbuilder.AddRedis("cache");',
            '/test/AppHost.cs'
        );
        const resources = parser.parseResources(doc);
        assert.strictEqual(resources.length, 1);
        assert.strictEqual(resources[0].name, 'cache');
        assert.strictEqual(resources[0].methodName, 'AddRedis');
    });

    test('parses multiple resource calls', () => {
        const parser = getCSharpParser();
        const doc = createMockDocument(
            [
                'var builder = DistributedApplication.CreateBuilder(args);',
                'var redis = builder.AddRedis("cache");',
                'var postgres = builder.AddPostgres("db");',
                'var api = builder.AddProject<Projects.Api>("api");',
                'builder.Build().Run();',
            ].join('\n'),
            '/test/AppHost.cs'
        );
        const resources = parser.parseResources(doc);
        assert.strictEqual(resources.length, 3);
        assert.strictEqual(resources[0].name, 'cache');
        assert.strictEqual(resources[0].methodName, 'AddRedis');
        assert.strictEqual(resources[1].name, 'db');
        assert.strictEqual(resources[1].methodName, 'AddPostgres');
        assert.strictEqual(resources[2].name, 'api');
        assert.strictEqual(resources[2].methodName, 'AddProject');
    });

    test('parses AddProject with generic type parameter', () => {
        const parser = getCSharpParser();
        const doc = createMockDocument(
            'var builder = DistributedApplication.CreateBuilder(args);\nbuilder.AddProject<Projects.WebFrontend>("webfrontend");',
            '/test/AppHost.cs'
        );
        const resources = parser.parseResources(doc);
        assert.strictEqual(resources.length, 1);
        assert.strictEqual(resources[0].name, 'webfrontend');
        assert.strictEqual(resources[0].methodName, 'AddProject');
    });

    test('parses AddContainer call', () => {
        const parser = getCSharpParser();
        const doc = createMockDocument(
            'var builder = DistributedApplication.CreateBuilder(args);\nbuilder.AddContainer("mycontainer", "myimage");',
            '/test/AppHost.cs'
        );
        const resources = parser.parseResources(doc);
        assert.strictEqual(resources.length, 1);
        assert.strictEqual(resources[0].name, 'mycontainer');
        assert.strictEqual(resources[0].methodName, 'AddContainer');
    });

    test('parses calls with whitespace variations', () => {
        const parser = getCSharpParser();
        const doc = createMockDocument(
            [
                'var builder = DistributedApplication.CreateBuilder(args);',
                'builder.AddRedis(  "spaced"  );',
                'builder.AddPostgres(',
                '    "multiline"',
                ');',
            ].join('\n'),
            '/test/AppHost.cs'
        );
        const resources = parser.parseResources(doc);
        assert.strictEqual(resources.length, 2);
        assert.strictEqual(resources[0].name, 'spaced');
        assert.strictEqual(resources[1].name, 'multiline');
    });

    test('parses chained calls', () => {
        const parser = getCSharpParser();
        const doc = createMockDocument(
            'var builder = DistributedApplication.CreateBuilder(args);\nbuilder.AddRedis("cache").WithEndpoint(port: 6379);',
            '/test/AppHost.cs'
        );
        const resources = parser.parseResources(doc);
        const addResources = resources.filter(r => r.kind === 'resource');
        assert.strictEqual(addResources.length, 1);
        assert.strictEqual(addResources[0].name, 'cache');
    });

    // --- parseResources: range accuracy ---

    test('range starts at dot before method name', () => {
        const parser = getCSharpParser();
        const line = 'builder.AddRedis("cache");';
        const doc = createMockDocument(line, '/test/AppHost.cs');
        const resources = parser.parseResources(doc);
        assert.strictEqual(resources.length, 1);
        // ".AddRedis("cache"" starts at index 7 (the dot)
        assert.strictEqual(resources[0].range.start.line, 0);
        assert.strictEqual(resources[0].range.start.character, 7);
    });

    test('range is on correct line for multi-line file', () => {
        const parser = getCSharpParser();
        const doc = createMockDocument(
            [
                'var builder = DistributedApplication.CreateBuilder(args);',
                '',
                '// Some comment',
                'builder.AddRedis("cache");',
            ].join('\n'),
            '/test/AppHost.cs'
        );
        const resources = parser.parseResources(doc);
        assert.strictEqual(resources.length, 1);
        assert.strictEqual(resources[0].range.start.line, 3, 'Resource should be on line 3');
    });

    // --- parseResources: statementStartLine ---

    test('statementStartLine equals range line for single-line call', () => {
        const parser = getCSharpParser();
        const doc = createMockDocument(
            'var builder = DistributedApplication.CreateBuilder(args);\nvar cache = builder.AddRedis("cache");',
            '/test/AppHost.cs'
        );
        const resources = parser.parseResources(doc);
        assert.strictEqual(resources.length, 1);
        assert.strictEqual(resources[0].statementStartLine, resources[0].range.start.line);
    });

    test('statementStartLine points to top of multi-line fluent chain', () => {
        const parser = getCSharpParser();
        const doc = createMockDocument(
            [
                'var builder = DistributedApplication.CreateBuilder(args);',
                '',
                'var cache = builder',
                '    .AddRedis("cache")',
                '    .WithLifetime(ContainerLifetime.Persistent);',
            ].join('\n'),
            '/test/AppHost.cs'
        );
        const resources = parser.parseResources(doc);
        const addResources = resources.filter(r => r.kind === 'resource');
        assert.strictEqual(addResources.length, 1);
        assert.strictEqual(addResources[0].range.start.line, 3, '.AddRedis is on line 3');
        assert.strictEqual(addResources[0].statementStartLine, 2, 'statement starts on line 2 (var cache = builder)');
    });

    test('statementStartLine works with multiple multi-line resources', () => {
        const parser = getCSharpParser();
        const doc = createMockDocument(
            [
                'var builder = DistributedApplication.CreateBuilder(args);',
                '',
                'var cache = builder',
                '    .AddRedis("cache")',
                '    .WithLifetime(ContainerLifetime.Persistent);',
                '',
                'var db = builder',
                '    .AddPostgres("postgres")',
                '    .AddDatabase("mydb");',
            ].join('\n'),
            '/test/AppHost.cs'
        );
        const resources = parser.parseResources(doc);
        assert.strictEqual(resources[0].statementStartLine, 2, 'cache starts at var cache');
        assert.strictEqual(resources[1].statementStartLine, 6, 'postgres starts at var db');
    });

    test('statementStartLine points to first line after opening brace', () => {
        const parser = getCSharpParser();
        const doc = createMockDocument(
            [
                'var builder = DistributedApplication.CreateBuilder(args);',
                '{',
                '    builder.AddRedis("cache");',
                '}',
            ].join('\n'),
            '/test/AppHost.cs'
        );
        const resources = parser.parseResources(doc);
        assert.strictEqual(resources.length, 1);
        assert.strictEqual(resources[0].statementStartLine, 2, 'statement starts after {');
    });

    test('statementStartLine skips single-line comments above statement', () => {
        const parser = getCSharpParser();
        const doc = createMockDocument(
            [
                'var builder = DistributedApplication.CreateBuilder(args);',
                '',
                '// This is the cache resource',
                '// It uses Redis',
                'var cache = builder',
                '    .AddRedis("cache")',
                '    .WithLifetime(ContainerLifetime.Persistent);',
            ].join('\n'),
            '/test/AppHost.cs'
        );
        const resources = parser.parseResources(doc);
        const addResources = resources.filter(r => r.kind === 'resource');
        assert.strictEqual(addResources.length, 1);
        assert.strictEqual(addResources[0].statementStartLine, 4, 'statement starts at var cache, skipping comments');
    });

    test('statementStartLine skips block comments above statement', () => {
        const parser = getCSharpParser();
        const doc = createMockDocument(
            [
                'var builder = DistributedApplication.CreateBuilder(args);',
                '',
                '/* Multi-line',
                ' * block comment',
                ' */',
                'var cache = builder',
                '    .AddRedis("cache");',
            ].join('\n'),
            '/test/AppHost.cs'
        );
        const resources = parser.parseResources(doc);
        assert.strictEqual(resources.length, 1);
        assert.strictEqual(resources[0].statementStartLine, 5, 'statement starts at var cache, skipping block comment');
    });

    // --- parseResources: empty / no matches ---

    test('returns empty array for file with no Add* calls', () => {
        const parser = getCSharpParser();
        const doc = createMockDocument(
            'var builder = DistributedApplication.CreateBuilder(args);\nbuilder.Build().Run();',
            '/test/AppHost.cs'
        );
        const resources = parser.parseResources(doc);
        assert.strictEqual(resources.length, 0);
    });

    test('returns empty array for empty document', () => {
        const parser = getCSharpParser();
        const doc = createMockDocument('', '/test/Empty.cs');
        const resources = parser.parseResources(doc);
        assert.strictEqual(resources.length, 0);
    });

    // --- parseResources: edge cases ---

    test('does not match non-Add methods', () => {
        const parser = getCSharpParser();
        const doc = createMockDocument(
            'builder.WithReference("notaresource");\nbuilder.ConfigureOpenTelemetry("otel");',
            '/test/AppHost.cs'
        );
        const resources = parser.parseResources(doc);
        assert.strictEqual(resources.length, 0);
    });

    test('does not match lowercase add (C# is PascalCase)', () => {
        const parser = getCSharpParser();
        const doc = createMockDocument(
            'builder.addRedis("cache");',
            '/test/AppHost.cs'
        );
        const resources = parser.parseResources(doc);
        assert.strictEqual(resources.length, 0, 'C# parser should only match PascalCase Add* methods');
    });

    test('matches Add* calls in commented-out code (known regex limitation)', () => {
        const parser = getCSharpParser();
        const doc = createMockDocument(
            [
                '// builder.AddRedis("commented-cache");',
                '/* builder.AddPostgres("block-commented"); */',
                'builder.AddRedis("real-cache");',
            ].join('\n'),
            '/test/AppHost.cs'
        );
        const resources = parser.parseResources(doc);
        // Regex-based parser cannot distinguish comments from code — all three match
        assert.strictEqual(resources.length, 3);
        assert.strictEqual(resources[2].name, 'real-cache');
    });

    test('does not match string interpolation expressions', () => {
        const parser = getCSharpParser();
        const doc = createMockDocument(
            'builder.AddRedis($"cache-{env}");',
            '/test/AppHost.cs'
        );
        const resources = parser.parseResources(doc);
        // $" prefix means the regex won't match (it expects .Add*("name" not .Add*($"name")
        assert.strictEqual(resources.length, 0, 'Interpolated strings should not match');
    });

    test('range end position is correct', () => {
        const parser = getCSharpParser();
        const doc = createMockDocument(
            'builder.AddRedis("cache");',
            '/test/AppHost.cs'
        );
        const resources = parser.parseResources(doc);
        assert.strictEqual(resources.length, 1);
        // .AddRedis("cache" = 17 chars starting at index 7, so end = 7 + 17 = 24
        assert.strictEqual(resources[0].range.end.line, 0);
        assert.strictEqual(resources[0].range.end.character, 24);
    });

    test('parses AddDatabase chained off a resource variable', () => {
        const parser = getCSharpParser();
        const doc = createMockDocument(
            'var postgres = builder.AddPostgres("pg");\nvar db = postgres.AddDatabase("mydb");',
            '/test/AppHost.cs'
        );
        const resources = parser.parseResources(doc);
        assert.strictEqual(resources.length, 2);
        assert.strictEqual(resources[0].name, 'pg');
        assert.strictEqual(resources[0].methodName, 'AddPostgres');
        assert.strictEqual(resources[1].name, 'mydb');
        assert.strictEqual(resources[1].methodName, 'AddDatabase');
    });

    test('handles single-quotes inside resource name gracefully', () => {
        const parser = getCSharpParser();
        // C# uses double quotes for strings, single quotes in the name itself
        const doc = createMockDocument(
            'builder.AddRedis("my-cache");',
            '/test/AppHost.cs'
        );
        const resources = parser.parseResources(doc);
        assert.strictEqual(resources.length, 1);
        assert.strictEqual(resources[0].name, 'my-cache');
    });

    test('parses resource names with hyphens and underscores', () => {
        const parser = getCSharpParser();
        const doc = createMockDocument(
            [
                'builder.AddRedis("my-cache");',
                'builder.AddPostgres("my_db");',
                'builder.AddRabbitMQ("event-bus-01");',
            ].join('\n'),
            '/test/AppHost.cs'
        );
        const resources = parser.parseResources(doc);
        assert.strictEqual(resources.length, 3);
        assert.strictEqual(resources[0].name, 'my-cache');
        assert.strictEqual(resources[1].name, 'my_db');
        assert.strictEqual(resources[2].name, 'event-bus-01');
    });

    test('parses AddXyz with complex generic parameters', () => {
        const parser = getCSharpParser();
        const doc = createMockDocument(
            'builder.AddProject<Projects.Services.Api>("api");',
            '/test/AppHost.cs'
        );
        const resources = parser.parseResources(doc);
        assert.strictEqual(resources.length, 1);
        assert.strictEqual(resources[0].name, 'api');
        assert.strictEqual(resources[0].methodName, 'AddProject');
    });

    test('parses realistic full AppHost file', () => {
        const parser = getCSharpParser();
        const doc = createMockDocument(
            [
                '#:sdk Aspire.AppHost.Sdk',
                '',
                'var builder = DistributedApplication.CreateBuilder(args);',
                '',
                'var cache = builder.AddRedis("cache");',
                '',
                'var db = builder.AddPostgres("postgres")',
                '    .AddDatabase("catalogdb");',
                '',
                'var api = builder.AddProject<Projects.CatalogApi>("catalogapi")',
                '    .WithReference(cache)',
                '    .WithReference(db);',
                '',
                'builder.AddProject<Projects.WebFrontend>("webfrontend")',
                '    .WithExternalHttpEndpoints()',
                '    .WithReference(api);',
                '',
                'builder.Build().Run();',
            ].join('\n'),
            '/test/AppHost.cs'
        );
        const resources = parser.parseResources(doc);
        const addResources = resources.filter(r => r.kind === 'resource');
        assert.strictEqual(addResources.length, 5);
        assert.strictEqual(addResources[0].name, 'cache');
        assert.strictEqual(addResources[0].methodName, 'AddRedis');
        assert.strictEqual(addResources[1].name, 'postgres');
        assert.strictEqual(addResources[1].methodName, 'AddPostgres');
        assert.strictEqual(addResources[2].name, 'catalogdb');
        assert.strictEqual(addResources[2].methodName, 'AddDatabase');
        assert.strictEqual(addResources[3].name, 'catalogapi');
        assert.strictEqual(addResources[3].methodName, 'AddProject');
        assert.strictEqual(addResources[4].name, 'webfrontend');
        assert.strictEqual(addResources[4].methodName, 'AddProject');

        // All should be classified as resources
        for (const r of addResources) {
            assert.strictEqual(r.kind, 'resource');
        }
    });

    // --- statementStartLine: preceding code blocks (issue #15618) ---

    test('statementStartLine not affected by preceding if block with braces', () => {
        const parser = getCSharpParser();
        const doc = createMockDocument(
            [
                'var builder = DistributedApplication.CreateBuilder(args);',
                '',
                'if (false)',
                '{',
                '    throw new Exception("This should not work");',
                '}',
                '',
                'builder.AddContainer("nginx", "nginx");',
            ].join('\n'),
            '/test/AppHost.cs'
        );
        const resources = parser.parseResources(doc);
        assert.strictEqual(resources.length, 1);
        assert.strictEqual(resources[0].name, 'nginx');
        assert.strictEqual(resources[0].statementStartLine, 7, 'statement should start on builder.AddContainer line, not inside the if block');
    });

    test('statementStartLine not affected by preceding nested braces', () => {
        const parser = getCSharpParser();
        const doc = createMockDocument(
            [
                'var builder = DistributedApplication.CreateBuilder(args);',
                '',
                'if (true)',
                '{',
                '    if (false)',
                '    {',
                '        throw new Exception("nested");',
                '    }',
                '}',
                '',
                'builder.AddContainer("nginx", "nginx");',
            ].join('\n'),
            '/test/AppHost.cs'
        );
        const resources = parser.parseResources(doc);
        assert.strictEqual(resources.length, 1);
        assert.strictEqual(resources[0].statementStartLine, 10, 'statement should start on builder.AddContainer line, not inside nested blocks');
    });

    test('statementStartLine not affected by preceding block with semicolons inside', () => {
        const parser = getCSharpParser();
        const doc = createMockDocument(
            [
                'var builder = DistributedApplication.CreateBuilder(args);',
                '',
                'if (true)',
                '{',
                '    Console.WriteLine("hello");',
                '    Console.WriteLine("world");',
                '}',
                '',
                'builder.AddContainer("nginx", "nginx");',
            ].join('\n'),
            '/test/AppHost.cs'
        );
        const resources = parser.parseResources(doc);
        assert.strictEqual(resources.length, 1);
        assert.strictEqual(resources[0].statementStartLine, 8, 'statement should start on builder.AddContainer line');
    });

    test('statementStartLine with comment between block and resource call', () => {
        const parser = getCSharpParser();
        const doc = createMockDocument(
            [
                'var builder = DistributedApplication.CreateBuilder(args);',
                '',
                'if (false)',
                '{',
                '    throw new Exception("fail");',
                '}',
                '',
                '// Add nginx container',
                'builder.AddContainer("nginx", "nginx");',
            ].join('\n'),
            '/test/AppHost.cs'
        );
        const resources = parser.parseResources(doc);
        assert.strictEqual(resources.length, 1);
        assert.strictEqual(resources[0].statementStartLine, 8, 'statement should start on builder.AddContainer line, skipping the comment');
    });

    test('statementStartLine with block comment between block and resource call', () => {
        const parser = getCSharpParser();
        const doc = createMockDocument(
            [
                'var builder = DistributedApplication.CreateBuilder(args);',
                '',
                'if (false)',
                '{',
                '    throw new Exception("fail");',
                '}',
                '',
                '/* Add nginx container */',
                'builder.AddContainer("nginx", "nginx");',
            ].join('\n'),
            '/test/AppHost.cs'
        );
        const resources = parser.parseResources(doc);
        assert.strictEqual(resources.length, 1);
        assert.strictEqual(resources[0].statementStartLine, 8, 'statement should start on builder.AddContainer line, skipping block comment');
    });

    test('statementStartLine with mixed comments between block and resource call', () => {
        const parser = getCSharpParser();
        const doc = createMockDocument(
            [
                'var builder = DistributedApplication.CreateBuilder(args);',
                '',
                'if (false)',
                '{',
                '    throw new Exception("fail");',
                '}',
                '',
                '// Line comment',
                '/* Block comment',
                ' * continuation',
                ' */',
                'builder.AddContainer("nginx", "nginx");',
            ].join('\n'),
            '/test/AppHost.cs'
        );
        const resources = parser.parseResources(doc);
        assert.strictEqual(resources.length, 1);
        assert.strictEqual(resources[0].statementStartLine, 11, 'statement should start on builder.AddContainer line, skipping all comments');
    });

    test('statementStartLine with comment between block and fluent chain', () => {
        const parser = getCSharpParser();
        const doc = createMockDocument(
            [
                'var builder = DistributedApplication.CreateBuilder(args);',
                '',
                'if (false)',
                '{',
                '    throw new Exception("fail");',
                '}',
                '',
                '// Add nginx',
                'var nginx = builder',
                '    .AddContainer("nginx", "nginx")',
                '    .WithEndpoint(80);',
            ].join('\n'),
            '/test/AppHost.cs'
        );
        const resources = parser.parseResources(doc);
        assert.strictEqual(resources.length, 1);
        assert.strictEqual(resources[0].statementStartLine, 8, 'statement should start at var nginx, skipping comment after block');
    });

    test('statementStartLine with multi-line fluent chain after a block', () => {
        const parser = getCSharpParser();
        const doc = createMockDocument(
            [
                'var builder = DistributedApplication.CreateBuilder(args);',
                '',
                'if (false)',
                '{',
                '    throw new Exception("fail");',
                '}',
                '',
                'var nginx = builder',
                '    .AddContainer("nginx", "nginx")',
                '    .WithEndpoint(80);',
            ].join('\n'),
            '/test/AppHost.cs'
        );
        const resources = parser.parseResources(doc);
        assert.strictEqual(resources.length, 1);
        assert.strictEqual(resources[0].statementStartLine, 7, 'statement should start at var nginx = builder, not inside the if block');
    });

    test('statementStartLine not affected by preceding try/catch block', () => {
        const parser = getCSharpParser();
        const doc = createMockDocument(
            [
                'var builder = DistributedApplication.CreateBuilder(args);',
                '',
                'try',
                '{',
                '    DoSomething();',
                '}',
                'catch (Exception ex)',
                '{',
                '    Console.WriteLine(ex);',
                '}',
                '',
                'builder.AddContainer("nginx", "nginx");',
            ].join('\n'),
            '/test/AppHost.cs'
        );
        const resources = parser.parseResources(doc);
        assert.strictEqual(resources.length, 1);
        assert.strictEqual(resources[0].statementStartLine, 11, 'statement should start on builder.AddContainer line, not inside try/catch');
    });

    test('statementStartLine not affected by preceding single-line block', () => {
        const parser = getCSharpParser();
        const doc = createMockDocument(
            [
                'var builder = DistributedApplication.CreateBuilder(args);',
                '',
                'if (false) { throw new Exception("fail"); }',
                '',
                'builder.AddContainer("nginx", "nginx");',
            ].join('\n'),
            '/test/AppHost.cs'
        );
        const resources = parser.parseResources(doc);
        assert.strictEqual(resources.length, 1);
        assert.strictEqual(resources[0].statementStartLine, 4, 'statement should start on builder.AddContainer line, not inside single-line block');
    });

    test('statementStartLine not affected by preceding empty block', () => {
        const parser = getCSharpParser();
        const doc = createMockDocument(
            [
                'var builder = DistributedApplication.CreateBuilder(args);',
                '',
                'if (true) { }',
                '',
                'builder.AddContainer("nginx", "nginx");',
            ].join('\n'),
            '/test/AppHost.cs'
        );
        const resources = parser.parseResources(doc);
        assert.strictEqual(resources.length, 1);
        assert.strictEqual(resources[0].statementStartLine, 4, 'statement should start on builder.AddContainer line, not inside empty block');
    });

    test('statementStartLine not affected by preceding for loop', () => {
        const parser = getCSharpParser();
        const doc = createMockDocument(
            [
                'var builder = DistributedApplication.CreateBuilder(args);',
                '',
                'for (int i = 0; i < 10; i++)',
                '{',
                '    Console.WriteLine(i);',
                '}',
                '',
                'builder.AddContainer("nginx", "nginx");',
            ].join('\n'),
            '/test/AppHost.cs'
        );
        const resources = parser.parseResources(doc);
        assert.strictEqual(resources.length, 1);
        assert.strictEqual(resources[0].statementStartLine, 7, 'statement should start on builder.AddContainer line, not inside for loop');
    });

    test('statementStartLine correct for multiple resources after blocks', () => {
        const parser = getCSharpParser();
        const doc = createMockDocument(
            [
                'var builder = DistributedApplication.CreateBuilder(args);',
                '',
                'if (false)',
                '{',
                '    throw new Exception("fail");',
                '}',
                '',
                'builder.AddContainer("nginx", "nginx");',
                '',
                'if (true)',
                '{',
                '    Console.WriteLine("ok");',
                '}',
                '',
                'builder.AddRedis("cache");',
            ].join('\n'),
            '/test/AppHost.cs'
        );
        const resources = parser.parseResources(doc);
        assert.strictEqual(resources.length, 2);
        assert.strictEqual(resources[0].statementStartLine, 7, 'first resource should start on its own line');
        assert.strictEqual(resources[1].statementStartLine, 14, 'second resource should start on its own line');
    });

    test('statementStartLine not affected by closing brace with trailing comment', () => {
        const parser = getCSharpParser();
        const doc = createMockDocument(
            [
                'var builder = DistributedApplication.CreateBuilder(args);',
                '',
                'if (false)',
                '{',
                '    throw new Exception("fail");',
                '} // end if',
                'builder.AddContainer("nginx", "nginx");',
            ].join('\n'),
            '/test/AppHost.cs'
        );
        const resources = parser.parseResources(doc);
        assert.strictEqual(resources.length, 1);
        assert.strictEqual(resources[0].statementStartLine, 6, 'statement should start on builder.AddContainer line, not on } // end if line');
    });

    test('statementStartLine reaches top of fluent chain through callback lambda', () => {
        const parser = getCSharpParser();
        const doc = createMockDocument(
            [
                'var builder = DistributedApplication.CreateBuilder(args);',
                '',
                'var catalogDb = builder.AddPostgres("postgres")',
                '                       .WithPgAdmin(resource => {',
                '                           resource.SomeConfig();',
                '                       })',
                '                       .AddDatabase("catalogdb");',
            ].join('\n'),
            '/test/AppHost.cs'
        );
        const resources = parser.parseResources(doc);
        assert.strictEqual(resources.length, 2);
        assert.strictEqual(resources[0].name, 'postgres');
        assert.strictEqual(resources[0].statementStartLine, 2, 'AddPostgres starts at var catalogDb');
        assert.strictEqual(resources[1].name, 'catalogdb');
        assert.strictEqual(resources[1].statementStartLine, 2, 'AddDatabase should also start at var catalogDb, not after callback }');
    });

    test('statementStartLine reaches top of fluent chain through RunAsContainer callback', () => {
        const parser = getCSharpParser();
        const doc = createMockDocument(
            [
                'var builder = DistributedApplication.CreateBuilder(args);',
                '',
                'var db = builder.AddPostgres("postgres")',
                '    .RunAsContainer(c => {',
                '        c.WithLifetime(ContainerLifetime.Persistent);',
                '    })',
                '    .AddDatabase("db");',
            ].join('\n'),
            '/test/AppHost.cs'
        );
        const resources = parser.parseResources(doc);
        assert.strictEqual(resources.length, 2);
        assert.strictEqual(resources[1].name, 'db');
        assert.strictEqual(resources[1].statementStartLine, 2, 'AddDatabase should start at var db, not after callback }');
    });

    test('statementStartLine not affected by } else { between block and resource', () => {
        const parser = getCSharpParser();
        const doc = createMockDocument(
            [
                'var builder = DistributedApplication.CreateBuilder(args);',
                '',
                'if (true)',
                '{',
                '    DoSomething();',
                '} else {',
                '    DoSomethingElse();',
                '}',
                '',
                'builder.AddContainer("nginx", "nginx");',
            ].join('\n'),
            '/test/AppHost.cs'
        );
        const resources = parser.parseResources(doc);
        assert.strictEqual(resources.length, 1);
        assert.strictEqual(resources[0].statementStartLine, 9, 'statement should start on builder.AddContainer line');
    });

    // --- Pipeline step classification ---

    test('classifies AddStep as pipelineStep', () => {
        const parser = getCSharpParser();
        const doc = createMockDocument(
            'builder.Pipeline.AddStep("assign-storage-role", async (context) => { });',
            '/test/AppHost.cs'
        );
        const resources = parser.parseResources(doc);
        assert.strictEqual(resources.length, 1);
        assert.strictEqual(resources[0].name, 'assign-storage-role');
        assert.strictEqual(resources[0].methodName, 'AddStep');
        assert.strictEqual(resources[0].kind, 'pipelineStep');
    });

    test('classifies AddRedis as resource, not pipelineStep', () => {
        const parser = getCSharpParser();
        const doc = createMockDocument(
            'builder.AddRedis("cache");',
            '/test/AppHost.cs'
        );
        const resources = parser.parseResources(doc);
        assert.strictEqual(resources.length, 1);
        assert.strictEqual(resources[0].kind, 'resource');
    });

    test('parses mixed resources and pipeline steps', () => {
        const parser = getCSharpParser();
        const doc = createMockDocument(
            [
                'var cache = builder.AddRedis("cache");',
                'builder.Pipeline.AddStep("deploy-step", async (ctx) => { });',
                'var db = builder.AddPostgres("pg");',
            ].join('\n'),
            '/test/AppHost.cs'
        );
        const resources = parser.parseResources(doc);
        assert.strictEqual(resources.length, 3);
        assert.strictEqual(resources[0].kind, 'resource');
        assert.strictEqual(resources[0].name, 'cache');
        assert.strictEqual(resources[1].kind, 'pipelineStep');
        assert.strictEqual(resources[1].name, 'deploy-step');
        assert.strictEqual(resources[2].kind, 'resource');
        assert.strictEqual(resources[2].name, 'pg');
    });
});

// ============================================================
// JS/TS Parser Tests
// ============================================================
suite('JsTsAppHostParser', () => {
    function getJsTsParser() {
        return getAllParsers().find(p => p.getSupportedExtensions().includes('.ts'))!;
    }

    // --- isAppHostFile ---

    test('detects AppHost via ES import from @aspire', () => {
        const parser = getJsTsParser();
        const doc = createMockDocument(
            'import { createBuilder } from "@aspire/sdk";\nconst builder = await createBuilder();',
            '/test/apphost.ts'
        );
        assert.strictEqual(parser.isAppHostFile(doc), true);
    });

    test('detects AppHost via require from @aspire', () => {
        const parser = getJsTsParser();
        const doc = createMockDocument(
            'const { createBuilder } = require("@aspire/sdk");\nconst builder = createBuilder();',
            '/test/apphost.js'
        );
        assert.strictEqual(parser.isAppHostFile(doc), true);
    });

    test('detects AppHost via single-quote import', () => {
        const parser = getJsTsParser();
        const doc = createMockDocument(
            "import { createBuilder } from '@aspire/sdk';",
            '/test/apphost.ts'
        );
        assert.strictEqual(parser.isAppHostFile(doc), true);
    });

    test('detects AppHost via local aspire module import', () => {
        const parser = getJsTsParser();
        const doc = createMockDocument(
            "import { createBuilder } from './.modules/aspire.js';\nconst builder = await createBuilder();",
            '/test/apphost.ts'
        );
        assert.strictEqual(parser.isAppHostFile(doc), true);
    });

    test('detects AppHost via createBuilder call without aspire import', () => {
        const parser = getJsTsParser();
        const doc = createMockDocument(
            "const builder = await createBuilder();\nawait builder.build().run();",
            '/test/apphost.ts'
        );
        assert.strictEqual(parser.isAppHostFile(doc), true);
    });

    test('rejects non-AppHost TS file', () => {
        const parser = getJsTsParser();
        const doc = createMockDocument(
            'import express from "express";\nconst app = express();',
            '/test/server.ts'
        );
        assert.strictEqual(parser.isAppHostFile(doc), false);
    });

    test('rejects non-AppHost JS file', () => {
        const parser = getJsTsParser();
        const doc = createMockDocument(
            'const http = require("http");\nhttp.createServer().listen(3000);',
            '/test/index.js'
        );
        assert.strictEqual(parser.isAppHostFile(doc), false);
    });

    // --- parseResources: basic patterns ---

    test('parses single addRedis call with double quotes', () => {
        const parser = getJsTsParser();
        const doc = createMockDocument(
            'import { createBuilder } from "@aspire/sdk";\nawait builder.addRedis("cache");',
            '/test/apphost.ts'
        );
        const resources = parser.parseResources(doc);
        assert.strictEqual(resources.length, 1);
        assert.strictEqual(resources[0].name, 'cache');
        assert.strictEqual(resources[0].methodName, 'addRedis');
    });

    test('parses single addRedis call with single quotes', () => {
        const parser = getJsTsParser();
        const doc = createMockDocument(
            "import { createBuilder } from '@aspire/sdk';\nawait builder.addRedis('cache');",
            '/test/apphost.ts'
        );
        const resources = parser.parseResources(doc);
        assert.strictEqual(resources.length, 1);
        assert.strictEqual(resources[0].name, 'cache');
    });

    test('parses multiple resource calls', () => {
        const parser = getJsTsParser();
        const doc = createMockDocument(
            [
                'import { createBuilder } from "@aspire/sdk";',
                'const builder = await createBuilder();',
                'await builder.addRedis("cache");',
                'await builder.addPostgres("db");',
                'await builder.addProject("api");',
            ].join('\n'),
            '/test/apphost.ts'
        );
        const resources = parser.parseResources(doc);
        assert.strictEqual(resources.length, 3);
        assert.strictEqual(resources[0].name, 'cache');
        assert.strictEqual(resources[0].methodName, 'addRedis');
        assert.strictEqual(resources[1].name, 'db');
        assert.strictEqual(resources[1].methodName, 'addPostgres');
        assert.strictEqual(resources[2].name, 'api');
        assert.strictEqual(resources[2].methodName, 'addProject');
    });

    test('parses chained calls', () => {
        const parser = getJsTsParser();
        const doc = createMockDocument(
            'import { createBuilder } from "@aspire/sdk";\nawait builder.addRedis("cache").withEndpoint(6379);',
            '/test/apphost.ts'
        );
        const resources = parser.parseResources(doc);
        assert.strictEqual(resources.length, 1);
        assert.strictEqual(resources[0].name, 'cache');
    });

    test('parses calls with whitespace variations', () => {
        const parser = getJsTsParser();
        const doc = createMockDocument(
            [
                'import { createBuilder } from "@aspire/sdk";',
                'await builder.addRedis(  "spaced"  );',
                'await builder.addPostgres(',
                '    "multiline"',
                ');',
            ].join('\n'),
            '/test/apphost.ts'
        );
        const resources = parser.parseResources(doc);
        assert.strictEqual(resources.length, 2);
        assert.strictEqual(resources[0].name, 'spaced');
        assert.strictEqual(resources[1].name, 'multiline');
    });

    // --- parseResources: range accuracy ---

    test('range starts at the dot before method name', () => {
        const parser = getJsTsParser();
        const line = 'await builder.addRedis("cache");';
        const doc = createMockDocument(line, '/test/apphost.ts');
        const resources = parser.parseResources(doc);
        assert.strictEqual(resources.length, 1);
        // ".addRedis("cache"" starts at index 13 (the dot after "await builder")
        assert.strictEqual(resources[0].range.start.line, 0);
        assert.strictEqual(resources[0].range.start.character, 13);
    });

    test('range is on correct line for multi-line file', () => {
        const parser = getJsTsParser();
        const doc = createMockDocument(
            [
                'import { createBuilder } from "@aspire/sdk";',
                '',
                '// comment',
                'await builder.addRedis("cache");',
            ].join('\n'),
            '/test/apphost.ts'
        );
        const resources = parser.parseResources(doc);
        assert.strictEqual(resources.length, 1);
        assert.strictEqual(resources[0].range.start.line, 3, 'Resource should be on line 3');
    });

    // --- parseResources: statementStartLine ---

    test('statementStartLine equals range line for single-line call', () => {
        const parser = getJsTsParser();
        const doc = createMockDocument(
            'import { createBuilder } from "@aspire/sdk";\nconst cache = await builder.addRedis("cache");',
            '/test/apphost.ts'
        );
        const resources = parser.parseResources(doc);
        assert.strictEqual(resources.length, 1);
        assert.strictEqual(resources[0].statementStartLine, resources[0].range.start.line);
    });

    test('statementStartLine points to top of multi-line fluent chain', () => {
        const parser = getJsTsParser();
        const doc = createMockDocument(
            [
                'import { createBuilder } from "@aspire/sdk";',
                '',
                'const cache = await builder',
                '    .addRedis("cache")',
                '    .withLifetime("persistent");',
            ].join('\n'),
            '/test/apphost.ts'
        );
        const resources = parser.parseResources(doc);
        assert.strictEqual(resources.length, 1);
        assert.strictEqual(resources[0].range.start.line, 3, '.addRedis is on line 3');
        assert.strictEqual(resources[0].statementStartLine, 2, 'statement starts on line 2 (const cache = await builder)');
    });

    test('statementStartLine works with multiple multi-line resources', () => {
        const parser = getJsTsParser();
        const doc = createMockDocument(
            [
                'import { createBuilder } from "@aspire/sdk";',
                '',
                'const cache = await builder',
                '    .addRedis("cache");',
                '',
                'const db = await builder',
                '    .addPostgres("postgres");',
            ].join('\n'),
            '/test/apphost.ts'
        );
        const resources = parser.parseResources(doc);
        assert.strictEqual(resources[0].statementStartLine, 2, 'cache starts at const cache');
        assert.strictEqual(resources[1].statementStartLine, 5, 'postgres starts at const db');
    });

    test('statementStartLine skips single-line comments above statement', () => {
        const parser = getJsTsParser();
        const doc = createMockDocument(
            [
                'import { createBuilder } from "@aspire/sdk";',
                '',
                '// ── Python Player ──────────────',
                '// A cunning Python player',
                'const pythonPlayer = await builder',
                '    .addPythonApp("python-player", "./python-player", "app.py")',
                '    .withHttpEndpoint({ env: "PORT" });',
            ].join('\n'),
            '/test/apphost.ts'
        );
        const resources = parser.parseResources(doc);
        assert.strictEqual(resources.length, 1);
        assert.strictEqual(resources[0].statementStartLine, 4, 'statement starts at const pythonPlayer, skipping comments');
    });

    test('statementStartLine skips block comments above statement', () => {
        const parser = getJsTsParser();
        const doc = createMockDocument(
            [
                'import { createBuilder } from "@aspire/sdk";',
                '',
                '/* Multi-line',
                ' * block comment',
                ' */',
                'const cache = await builder',
                '    .addRedis("cache");',
            ].join('\n'),
            '/test/apphost.ts'
        );
        const resources = parser.parseResources(doc);
        assert.strictEqual(resources.length, 1);
        assert.strictEqual(resources[0].statementStartLine, 5, 'statement starts at const cache, skipping block comment');
    });

    // --- parseResources: empty / no matches ---

    test('returns empty array for file with no add* calls', () => {
        const parser = getJsTsParser();
        const doc = createMockDocument(
            'import { createBuilder } from "@aspire/sdk";\nconst builder = await createBuilder();',
            '/test/apphost.ts'
        );
        const resources = parser.parseResources(doc);
        assert.strictEqual(resources.length, 0);
    });

    test('returns empty array for empty document', () => {
        const parser = getJsTsParser();
        const doc = createMockDocument('', '/test/empty.ts');
        const resources = parser.parseResources(doc);
        assert.strictEqual(resources.length, 0);
    });

    // --- parseResources: edge cases ---

    test('does not match non-add methods', () => {
        const parser = getJsTsParser();
        const doc = createMockDocument(
            'builder.withReference("notaresource");\nbuilder.configureOpenTelemetry("otel");',
            '/test/apphost.ts'
        );
        const resources = parser.parseResources(doc);
        assert.strictEqual(resources.length, 0);
    });

    test('matches add* calls in commented-out code (known regex limitation)', () => {
        const parser = getJsTsParser();
        const doc = createMockDocument(
            [
                '// await builder.addRedis("commented-cache");',
                '/* await builder.addPostgres("block-commented"); */',
                'await builder.addRedis("real-cache");',
            ].join('\n'),
            '/test/apphost.ts'
        );
        const resources = parser.parseResources(doc);
        // Regex-based parser cannot distinguish comments from code — all three match
        assert.strictEqual(resources.length, 3);
        assert.strictEqual(resources[2].name, 'real-cache');
    });

    test('does not match template literal arguments', () => {
        const parser = getJsTsParser();
        const doc = createMockDocument(
            'await builder.addRedis(`cache`);',
            '/test/apphost.ts'
        );
        const resources = parser.parseResources(doc);
        assert.strictEqual(resources.length, 0, 'Template literals should not match');
    });

    test('range end position is correct', () => {
        const parser = getJsTsParser();
        const doc = createMockDocument(
            'await builder.addRedis("cache"',
            '/test/apphost.ts'
        );
        const resources = parser.parseResources(doc);
        assert.strictEqual(resources.length, 1);
        // .addRedis("cache" = 17 chars starting at index 13, so end = 13 + 17 = 30
        assert.strictEqual(resources[0].range.end.line, 0);
        assert.strictEqual(resources[0].range.end.character, 30);
    });

    test('parses resource names with hyphens and underscores', () => {
        const parser = getJsTsParser();
        const doc = createMockDocument(
            [
                'await builder.addRedis("my-cache");',
                'await builder.addPostgres("my_db");',
                'await builder.addRabbitMQ("event-bus-01");',
            ].join('\n'),
            '/test/apphost.ts'
        );
        const resources = parser.parseResources(doc);
        assert.strictEqual(resources.length, 3);
        assert.strictEqual(resources[0].name, 'my-cache');
        assert.strictEqual(resources[1].name, 'my_db');
        assert.strictEqual(resources[2].name, 'event-bus-01');
    });

    test('case-insensitive match for add* methods', () => {
        const parser = getJsTsParser();
        const doc = createMockDocument(
            'await builder.AddRedis("upper");\nawait builder.addRedis("lower");',
            '/test/apphost.ts'
        );
        const resources = parser.parseResources(doc);
        // Both should match because JS/TS regex uses /gi flag
        assert.strictEqual(resources.length, 2);
        assert.strictEqual(resources[0].name, 'upper');
        assert.strictEqual(resources[1].name, 'lower');
    });

    test('does not match mismatched quotes', () => {
        const parser = getJsTsParser();
        // Mismatched quotes should NOT be parsed (the regex requires matching quote chars)
        const doc = createMockDocument(
            'await builder.addRedis("cache\');',
            '/test/apphost.ts'
        );
        const resources = parser.parseResources(doc);
        assert.strictEqual(resources.length, 0);
    });

    test('parses .js file correctly', () => {
        const parser = getJsTsParser();
        const doc = createMockDocument(
            [
                'const { createBuilder } = require("@aspire/sdk");',
                'const builder = createBuilder();',
                'builder.addRedis("cache");',
                'builder.addPostgres("db");',
            ].join('\n'),
            '/test/apphost.js'
        );
        const resources = parser.parseResources(doc);
        assert.strictEqual(resources.length, 2);
        assert.strictEqual(resources[0].name, 'cache');
        assert.strictEqual(resources[1].name, 'db');
    });

    test('parses realistic full AppHost TS file', () => {
        const parser = getJsTsParser();
        const doc = createMockDocument(
            [
                'import { createBuilder } from "@aspire/sdk";',
                '',
                'const builder = await createBuilder();',
                '',
                'const cache = await builder.addRedis("cache");',
                '',
                'const db = await builder.addPostgres("postgres")',
                '    .addDatabase("catalogdb");',
                '',
                'const api = await builder.addProject("catalogapi")',
                '    .withReference(cache)',
                '    .withReference(db);',
                '',
                'await builder.addProject("webfrontend")',
                '    .withExternalHttpEndpoints()',
                '    .withReference(api);',
                '',
                'await builder.build().run();',
            ].join('\n'),
            '/test/apphost.ts'
        );
        const resources = parser.parseResources(doc);
        assert.strictEqual(resources.length, 5);
        assert.strictEqual(resources[0].name, 'cache');
        assert.strictEqual(resources[1].name, 'postgres');
        assert.strictEqual(resources[2].name, 'catalogdb');
        assert.strictEqual(resources[3].name, 'catalogapi');
        assert.strictEqual(resources[4].name, 'webfrontend');

        // All should be classified as resources
        for (const r of resources) {
            assert.strictEqual(r.kind, 'resource');
        }
    });

    // --- statementStartLine: preceding code blocks (issue #15618) ---

    test('statementStartLine not affected by preceding if block with braces', () => {
        const parser = getJsTsParser();
        const doc = createMockDocument(
            [
                'import { createBuilder } from "@aspire/sdk";',
                '',
                'if (false)',
                '{',
                '    throw new Error("This should not work");',
                '}',
                '',
                'await builder.addContainer("nginx", "nginx");',
            ].join('\n'),
            '/test/apphost.ts'
        );
        const resources = parser.parseResources(doc);
        assert.strictEqual(resources.length, 1);
        assert.strictEqual(resources[0].name, 'nginx');
        assert.strictEqual(resources[0].statementStartLine, 7, 'statement should start on builder.addContainer line, not inside the if block');
    });

    test('statementStartLine not affected by preceding nested braces', () => {
        const parser = getJsTsParser();
        const doc = createMockDocument(
            [
                'import { createBuilder } from "@aspire/sdk";',
                '',
                'if (true)',
                '{',
                '    if (false)',
                '    {',
                '        throw new Error("nested");',
                '    }',
                '}',
                '',
                'await builder.addContainer("nginx", "nginx");',
            ].join('\n'),
            '/test/apphost.ts'
        );
        const resources = parser.parseResources(doc);
        assert.strictEqual(resources.length, 1);
        assert.strictEqual(resources[0].statementStartLine, 10, 'statement should start on builder.addContainer line, not inside nested blocks');
    });

    test('statementStartLine not affected by preceding block with semicolons inside', () => {
        const parser = getJsTsParser();
        const doc = createMockDocument(
            [
                'import { createBuilder } from "@aspire/sdk";',
                '',
                'if (true)',
                '{',
                '    console.log("hello");',
                '    console.log("world");',
                '}',
                '',
                'await builder.addContainer("nginx", "nginx");',
            ].join('\n'),
            '/test/apphost.ts'
        );
        const resources = parser.parseResources(doc);
        assert.strictEqual(resources.length, 1);
        assert.strictEqual(resources[0].statementStartLine, 8, 'statement should start on builder.addContainer line');
    });

    test('statementStartLine with comment between block and resource call', () => {
        const parser = getJsTsParser();
        const doc = createMockDocument(
            [
                'import { createBuilder } from "@aspire/sdk";',
                '',
                'if (false)',
                '{',
                '    throw new Error("fail");',
                '}',
                '',
                '// Add nginx container',
                'await builder.addContainer("nginx", "nginx");',
            ].join('\n'),
            '/test/apphost.ts'
        );
        const resources = parser.parseResources(doc);
        assert.strictEqual(resources.length, 1);
        assert.strictEqual(resources[0].statementStartLine, 8, 'statement should start on builder.addContainer line, skipping the comment');
    });

    test('statementStartLine with block comment between block and resource call', () => {
        const parser = getJsTsParser();
        const doc = createMockDocument(
            [
                'import { createBuilder } from "@aspire/sdk";',
                '',
                'if (false)',
                '{',
                '    throw new Error("fail");',
                '}',
                '',
                '/* Add nginx container */',
                'await builder.addContainer("nginx", "nginx");',
            ].join('\n'),
            '/test/apphost.ts'
        );
        const resources = parser.parseResources(doc);
        assert.strictEqual(resources.length, 1);
        assert.strictEqual(resources[0].statementStartLine, 8, 'statement should start on builder.addContainer line, skipping block comment');
    });

    test('statementStartLine with mixed comments between block and resource call', () => {
        const parser = getJsTsParser();
        const doc = createMockDocument(
            [
                'import { createBuilder } from "@aspire/sdk";',
                '',
                'if (false)',
                '{',
                '    throw new Error("fail");',
                '}',
                '',
                '// Line comment',
                '/* Block comment',
                ' * continuation',
                ' */',
                'await builder.addContainer("nginx", "nginx");',
            ].join('\n'),
            '/test/apphost.ts'
        );
        const resources = parser.parseResources(doc);
        assert.strictEqual(resources.length, 1);
        assert.strictEqual(resources[0].statementStartLine, 11, 'statement should start on builder.addContainer line, skipping all comments');
    });

    test('statementStartLine with comment between block and fluent chain', () => {
        const parser = getJsTsParser();
        const doc = createMockDocument(
            [
                'import { createBuilder } from "@aspire/sdk";',
                '',
                'if (false)',
                '{',
                '    throw new Error("fail");',
                '}',
                '',
                '// Add nginx',
                'const nginx = await builder',
                '    .addContainer("nginx", "nginx")',
                '    .withEndpoint(80);',
            ].join('\n'),
            '/test/apphost.ts'
        );
        const resources = parser.parseResources(doc);
        assert.strictEqual(resources.length, 1);
        assert.strictEqual(resources[0].statementStartLine, 8, 'statement should start at const nginx, skipping comment after block');
    });

    test('statementStartLine with multi-line fluent chain after a block', () => {
        const parser = getJsTsParser();
        const doc = createMockDocument(
            [
                'import { createBuilder } from "@aspire/sdk";',
                '',
                'if (false)',
                '{',
                '    throw new Error("fail");',
                '}',
                '',
                'const nginx = await builder',
                '    .addContainer("nginx", "nginx")',
                '    .withEndpoint(80);',
            ].join('\n'),
            '/test/apphost.ts'
        );
        const resources = parser.parseResources(doc);
        assert.strictEqual(resources.length, 1);
        assert.strictEqual(resources[0].statementStartLine, 7, 'statement should start at const nginx = await builder, not inside the if block');
    });

    test('statementStartLine not affected by preceding try/catch block', () => {
        const parser = getJsTsParser();
        const doc = createMockDocument(
            [
                'import { createBuilder } from "@aspire/sdk";',
                '',
                'try',
                '{',
                '    doSomething();',
                '}',
                'catch (e)',
                '{',
                '    console.error(e);',
                '}',
                '',
                'await builder.addContainer("nginx", "nginx");',
            ].join('\n'),
            '/test/apphost.ts'
        );
        const resources = parser.parseResources(doc);
        assert.strictEqual(resources.length, 1);
        assert.strictEqual(resources[0].statementStartLine, 11, 'statement should start on builder.addContainer line, not inside try/catch');
    });

    test('statementStartLine not affected by preceding single-line block', () => {
        const parser = getJsTsParser();
        const doc = createMockDocument(
            [
                'import { createBuilder } from "@aspire/sdk";',
                '',
                'if (false) { throw new Error("fail"); }',
                '',
                'await builder.addContainer("nginx", "nginx");',
            ].join('\n'),
            '/test/apphost.ts'
        );
        const resources = parser.parseResources(doc);
        assert.strictEqual(resources.length, 1);
        assert.strictEqual(resources[0].statementStartLine, 4, 'statement should start on builder.addContainer line, not inside single-line block');
    });

    test('statementStartLine not affected by preceding empty block', () => {
        const parser = getJsTsParser();
        const doc = createMockDocument(
            [
                'import { createBuilder } from "@aspire/sdk";',
                '',
                'if (true) { }',
                '',
                'await builder.addContainer("nginx", "nginx");',
            ].join('\n'),
            '/test/apphost.ts'
        );
        const resources = parser.parseResources(doc);
        assert.strictEqual(resources.length, 1);
        assert.strictEqual(resources[0].statementStartLine, 4, 'statement should start on builder.addContainer line, not inside empty block');
    });

    test('statementStartLine not affected by preceding for loop', () => {
        const parser = getJsTsParser();
        const doc = createMockDocument(
            [
                'import { createBuilder } from "@aspire/sdk";',
                '',
                'for (let i = 0; i < 10; i++)',
                '{',
                '    console.log(i);',
                '}',
                '',
                'await builder.addContainer("nginx", "nginx");',
            ].join('\n'),
            '/test/apphost.ts'
        );
        const resources = parser.parseResources(doc);
        assert.strictEqual(resources.length, 1);
        assert.strictEqual(resources[0].statementStartLine, 7, 'statement should start on builder.addContainer line, not inside for loop');
    });

    test('statementStartLine correct for multiple resources after blocks', () => {
        const parser = getJsTsParser();
        const doc = createMockDocument(
            [
                'import { createBuilder } from "@aspire/sdk";',
                '',
                'if (false)',
                '{',
                '    throw new Error("fail");',
                '}',
                '',
                'await builder.addContainer("nginx", "nginx");',
                '',
                'if (true)',
                '{',
                '    console.log("ok");',
                '}',
                '',
                'await builder.addRedis("cache");',
            ].join('\n'),
            '/test/apphost.ts'
        );
        const resources = parser.parseResources(doc);
        assert.strictEqual(resources.length, 2);
        assert.strictEqual(resources[0].statementStartLine, 7, 'first resource should start on its own line');
        assert.strictEqual(resources[1].statementStartLine, 14, 'second resource should start on its own line');
    });

    test('statementStartLine not affected by closing brace with trailing comment', () => {
        const parser = getJsTsParser();
        const doc = createMockDocument(
            [
                'import { createBuilder } from "@aspire/sdk";',
                '',
                'if (false)',
                '{',
                '    throw new Error("fail");',
                '} // end if',
                'await builder.addContainer("nginx", "nginx");',
            ].join('\n'),
            '/test/apphost.ts'
        );
        const resources = parser.parseResources(doc);
        assert.strictEqual(resources.length, 1);
        assert.strictEqual(resources[0].statementStartLine, 6, 'statement should start on builder.addContainer line, not on } // end if line');
    });

    test('statementStartLine reaches top of fluent chain through callback arrow function', () => {
        const parser = getJsTsParser();
        const doc = createMockDocument(
            [
                'import { createBuilder } from "@aspire/sdk";',
                '',
                'const catalogDb = await builder.addPostgres("postgres")',
                '    .withPgAdmin((resource) => {',
                '        resource.someConfig();',
                '    })',
                '    .addDatabase("catalogdb");',
            ].join('\n'),
            '/test/apphost.ts'
        );
        const resources = parser.parseResources(doc);
        assert.strictEqual(resources.length, 2);
        assert.strictEqual(resources[0].name, 'postgres');
        assert.strictEqual(resources[0].statementStartLine, 2, 'addPostgres starts at const catalogDb');
        assert.strictEqual(resources[1].name, 'catalogdb');
        assert.strictEqual(resources[1].statementStartLine, 2, 'addDatabase should also start at const catalogDb, not after callback }');
    });

    test('statementStartLine reaches top of fluent chain through runAsContainer callback', () => {
        const parser = getJsTsParser();
        const doc = createMockDocument(
            [
                'import { createBuilder } from "@aspire/sdk";',
                '',
                'const db = await builder.addPostgres("postgres")',
                '    .runAsContainer((c) => {',
                '        c.withLifetime("persistent");',
                '    })',
                '    .addDatabase("db");',
            ].join('\n'),
            '/test/apphost.ts'
        );
        const resources = parser.parseResources(doc);
        assert.strictEqual(resources.length, 2);
        assert.strictEqual(resources[1].name, 'db');
        assert.strictEqual(resources[1].statementStartLine, 2, 'addDatabase should start at const db, not after callback }');
    });

    test('statementStartLine not affected by } else { between block and resource', () => {
        const parser = getJsTsParser();
        const doc = createMockDocument(
            [
                'import { createBuilder } from "@aspire/sdk";',
                '',
                'if (true)',
                '{',
                '    doSomething();',
                '} else {',
                '    doSomethingElse();',
                '}',
                '',
                'await builder.addContainer("nginx", "nginx");',
            ].join('\n'),
            '/test/apphost.ts'
        );
        const resources = parser.parseResources(doc);
        assert.strictEqual(resources.length, 1);
        assert.strictEqual(resources[0].statementStartLine, 9, 'statement should start on builder.addContainer line');
    });

    // --- Pipeline step classification ---

    test('classifies addStep as pipelineStep', () => {
        const parser = getJsTsParser();
        const doc = createMockDocument(
            'import { createBuilder } from "@aspire/sdk";\nbuilder.pipeline.addStep("deploy-step", async (ctx) => { });',
            '/test/apphost.ts'
        );
        const resources = parser.parseResources(doc);
        assert.strictEqual(resources.length, 1);
        assert.strictEqual(resources[0].name, 'deploy-step');
        assert.strictEqual(resources[0].methodName, 'addStep');
        assert.strictEqual(resources[0].kind, 'pipelineStep');
    });

    test('classifies addRedis as resource, not pipelineStep', () => {
        const parser = getJsTsParser();
        const doc = createMockDocument(
            'import { createBuilder } from "@aspire/sdk";\nawait builder.addRedis("cache");',
            '/test/apphost.ts'
        );
        const resources = parser.parseResources(doc);
        assert.strictEqual(resources.length, 1);
        assert.strictEqual(resources[0].kind, 'resource');
    });

    test('classifies AddStep case-insensitively as pipelineStep', () => {
        const parser = getJsTsParser();
        const doc = createMockDocument(
            'import { createBuilder } from "@aspire/sdk";\nbuilder.pipeline.AddStep("my-step", async (ctx) => { });',
            '/test/apphost.ts'
        );
        const resources = parser.parseResources(doc);
        assert.strictEqual(resources.length, 1);
        assert.strictEqual(resources[0].kind, 'pipelineStep');
    });

    test('parses mixed resources and pipeline steps', () => {
        const parser = getJsTsParser();
        const doc = createMockDocument(
            [
                'import { createBuilder } from "@aspire/sdk";',
                'const cache = await builder.addRedis("cache");',
                'builder.pipeline.addStep("deploy", async (ctx) => { });',
                'const db = await builder.addPostgres("pg");',
            ].join('\n'),
            '/test/apphost.ts'
        );
        const resources = parser.parseResources(doc);
        assert.strictEqual(resources.length, 3);
        assert.strictEqual(resources[0].kind, 'resource');
        assert.strictEqual(resources[0].name, 'cache');
        assert.strictEqual(resources[1].kind, 'pipelineStep');
        assert.strictEqual(resources[1].name, 'deploy');
        assert.strictEqual(resources[2].kind, 'resource');
        assert.strictEqual(resources[2].name, 'pg');
    });
});

// ============================================================
// findBuilderStatementLine tests
// ============================================================
suite('findBuilderStatementLine', () => {
    function getCSharpParser() {
        return getAllParsers().find(p => p.getSupportedExtensions().includes('.cs'))!;
    }

    function getJsTsParser() {
        return getAllParsers().find(p => p.getSupportedExtensions().includes('.ts'))!;
    }

    test('C# parser finds builder line for var declaration', () => {
        const parser = getCSharpParser();
        const doc = createMockDocument(
            [
                '// header comment',
                'var builder = DistributedApplication.CreateBuilder(args);',
                'builder.AddRedis("cache");',
            ].join('\n'),
            '/test/AppHost.cs'
        );
        assert.strictEqual(parser.findBuilderStatementLine?.(doc), 1);
    });

    test('C# parser finds builder line for #:sdk file with leading directives', () => {
        const parser = getCSharpParser();
        const doc = createMockDocument(
            [
                '#:sdk Aspire.AppHost.Sdk',
                '',
                'var builder = DistributedApplication.CreateBuilder(args);',
                'builder.AddRedis("cache");',
            ].join('\n'),
            '/test/AppHost.cs'
        );
        assert.strictEqual(parser.findBuilderStatementLine?.(doc), 2);
    });

    test('C# parser returns undefined when no CreateBuilder is present', () => {
        const parser = getCSharpParser();
        const doc = createMockDocument(
            'using System;\nclass Foo { }',
            '/test/Foo.cs'
        );
        assert.strictEqual(parser.findBuilderStatementLine?.(doc), undefined);
    });

    test('JS/TS parser finds builder line for createBuilder call', () => {
        const parser = getJsTsParser();
        const doc = createMockDocument(
            [
                'import { createBuilder } from "@aspire/sdk";',
                '',
                'const builder = createBuilder();',
                'await builder.addRedis("cache");',
            ].join('\n'),
            '/test/apphost.ts'
        );
        assert.strictEqual(parser.findBuilderStatementLine?.(doc), 2);
    });

    test('JS/TS parser returns undefined when no createBuilder call is present', () => {
        const parser = getJsTsParser();
        const doc = createMockDocument(
            'import express from "express";\nconst app = express();',
            '/test/server.ts'
        );
        assert.strictEqual(parser.findBuilderStatementLine?.(doc), undefined);
    });
});
