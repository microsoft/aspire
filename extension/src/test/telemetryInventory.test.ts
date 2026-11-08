import * as assert from 'assert';
import * as fs from 'fs';
import * as path from 'path';
import * as ts from 'typescript';

type TelemetryInventory = {
    events: Record<string, Record<string, unknown>>;
    commonProperties: Record<string, unknown>;
};

type TelemetryRegistryEvent = {
    name: string;
    entries: string[];
};

type ResourceDebugTelemetryPropertyExpectation = {
    eventName: string;
    interfaceName: string;
    propertyName: string;
    values: readonly string[];
    comment: string;
};

// Telemetry events emit verbatim to the wire — the registry-declared name
// (e.g. `aspire/vscode/command/invoked`, `aspire/dashboard/operation`) is
// what appears in `extension/telemetry.json`. The transport sender strips VS
// Code's automatic `<extensionId>/` prefix after the TelemetryLogger has
// applied its platform guarantees.
const telemetryEntityPrefix = '';
const freeformPropertyNamePattern = /(?:^|_)(?:path|message|description|args?)(?:_|$)/i;
const platformCommonTelemetryProperties = [
    'common.devDeviceId',
    'common.extname',
    'common.extversion',
    'common.isAgentsWindow',
    'common.isnewappinstall',
    'common.nodeArch',
    'common.os',
    'common.platformversion',
    'common.product',
    'common.remotename',
    'common.sqmid',
    'common.telemetryclientversion',
    'common.uikind',
    'common.vscodecommithash',
    'common.vscodemachineid',
    'common.vscodereleasedate',
    'common.vscodesessionid',
    'common.vscodeversion',
] as const;
const resourceDebugTelemetryPropertyExpectations: readonly ResourceDebugTelemetryPropertyExpectation[] = [
    {
        eventName: 'aspire/vscode/resourcedebug/start',
        interfaceName: 'ResourceDebugStartTelemetryProperties',
        propertyName: 'requested_strategy',
        values: ['attach', 'auto', 'invalid'],
        comment: 'The bounded resource debug strategy requested by the caller: auto, attach, or invalid.',
    },
    {
        eventName: 'aspire/vscode/resourcedebug/result',
        interfaceName: 'ResourceDebugResultTelemetryProperties',
        propertyName: 'requested_strategy',
        values: ['attach', 'auto', 'invalid'],
        comment: 'The bounded resource debug strategy requested by the caller: auto, attach, or invalid.',
    },
    {
        eventName: 'aspire/vscode/resourcedebug/result',
        interfaceName: 'ResourceDebugResultTelemetryProperties',
        propertyName: 'effective_strategy',
        values: ['attach', 'none'],
        comment: 'The bounded effective resource debug strategy: attach or none.',
    },
    {
        eventName: 'aspire/vscode/resourcedebug/session/end',
        interfaceName: 'ResourceDebugSessionEndTelemetryProperties',
        propertyName: 'requested_strategy',
        values: ['attach', 'auto'],
        comment: 'The bounded resource debug strategy requested by the caller: auto or attach.',
    },
    {
        eventName: 'aspire/vscode/resourcedebug/session/end',
        interfaceName: 'ResourceDebugSessionEndTelemetryProperties',
        propertyName: 'effective_strategy',
        values: ['attach'],
        comment: 'The bounded effective resource debug strategy: attach.',
    },
];

function readTelemetryInventory(): TelemetryInventory {
    const inventoryPath = path.resolve(__dirname, '../../telemetry.json');
    return JSON.parse(fs.readFileSync(inventoryPath, 'utf8')) as TelemetryInventory;
}

function readResourceDebugTelemetryPropertyValues(interfaceName: string, propertyName: string): string[] {
    const telemetryPath = path.resolve(__dirname, '../../src/debugger/resourceDebugTelemetry.ts');
    const program = ts.createProgram([telemetryPath], {
        moduleResolution: ts.ModuleResolutionKind.Node10,
        target: ts.ScriptTarget.Latest,
    });
    const sourceFile = program.getSourceFile(telemetryPath);
    const telemetryInterface = sourceFile?.statements.find((node): node is ts.InterfaceDeclaration =>
        ts.isInterfaceDeclaration(node) && node.name.text === interfaceName);
    if (!telemetryInterface) {
        return [];
    }

    const typeChecker = program.getTypeChecker();
    const property = typeChecker
        .getTypeAtLocation(telemetryInterface)
        .getProperty(propertyName);
    if (!property) {
        return [];
    }

    const declaration = property.valueDeclaration ?? property.declarations?.[0];
    if (!declaration) {
        return [];
    }

    return getStringLiteralValues(typeChecker.getTypeOfSymbolAtLocation(property, declaration));
}

function readTelemetryRegistryEvents(): TelemetryRegistryEvent[] {
    const registryPath = path.resolve(__dirname, '../../src/utils/telemetryRegistry.ts');
    const sourceText = fs.readFileSync(registryPath, 'utf8');
    const sourceFile = ts.createSourceFile(registryPath, sourceText, ts.ScriptTarget.Latest, true);
    const events: TelemetryRegistryEvent[] = [];

    sourceFile.forEachChild(node => {
        if (!ts.isInterfaceDeclaration(node) || node.name.text !== 'TelemetryEventSchema') {
            return;
        }

        for (const member of node.members) {
            if (!ts.isPropertySignature(member) || !member.type || !ts.isStringLiteral(member.name)) {
                continue;
            }

            const entries = getSchemaEntries(member.type);
            events.push({ name: member.name.text, entries });
        }
    });

    return events;
}

function readCommonTelemetryProperties(): string[] {
    const registryPath = path.resolve(__dirname, '../../src/utils/telemetryRegistry.ts');
    const sourceText = fs.readFileSync(registryPath, 'utf8');
    const sourceFile = ts.createSourceFile(registryPath, sourceText, ts.ScriptTarget.Latest, true);

    for (const node of sourceFile.statements) {
        if (ts.isTypeAliasDeclaration(node) && node.name.text === 'CommonTelemetryProperty') {
            return getStringLiteralUnion(node.type).sort();
        }
    }

    return [];
}

function getSchemaEntries(typeNode: ts.TypeNode): string[] {
    if (!ts.isTypeLiteralNode(typeNode)) {
        return [];
    }

    const entries = new Set<string>();

    for (const member of typeNode.members) {
        if (!ts.isPropertySignature(member) || !member.type || !ts.isIdentifier(member.name)) {
            continue;
        }

        if (member.name.text !== 'properties' && member.name.text !== 'measurements') {
            continue;
        }

        for (const entry of getStringLiteralUnion(member.type)) {
            entries.add(entry);
        }
    }

    return [...entries].sort();
}

function getStringLiteralUnion(typeNode: ts.TypeNode): string[] {
    if (typeNode.kind === ts.SyntaxKind.NeverKeyword) {
        return [];
    }

    if (ts.isLiteralTypeNode(typeNode) && ts.isStringLiteral(typeNode.literal)) {
        return [typeNode.literal.text];
    }

    if (ts.isUnionTypeNode(typeNode)) {
        return typeNode.types.flatMap(getStringLiteralUnion);
    }

    if (ts.isParenthesizedTypeNode(typeNode)) {
        return getStringLiteralUnion(typeNode.type);
    }

    return [];
}

function getStringLiteralValues(type: ts.Type): string[] {
    if (type.isUnion()) {
        return [...new Set(type.types.flatMap(getStringLiteralValues))].sort();
    }

    return type.flags & ts.TypeFlags.StringLiteral
        ? [(type as ts.StringLiteralType).value]
        : [];
}

suite('extension/telemetry.json', () => {
    test('event entity names are lowercase', () => {
        const inventory = readTelemetryInventory();
        const mixedCaseEntityNames = Object.keys(inventory.events)
            .filter(name => name !== name.toLowerCase());

        assert.deepStrictEqual(mixedCaseEntityNames, []);
    });

    test('declares every event property from telemetry registry', () => {
        const inventory = readTelemetryInventory();
        const missingInventoryEntries = readTelemetryRegistryEvents()
            .flatMap(event => {
                const inventoryEvent = inventory.events[`${telemetryEntityPrefix}${event.name}`];

                return event.entries
                    .filter(entry => !Object.hasOwn(inventoryEvent ?? {}, entry))
                    .map(entry => `${event.name}.${entry}`);
            });

        assert.deepStrictEqual(missingInventoryEntries, []);
    });

    test('declares every common property from telemetry registry', () => {
        const inventory = readTelemetryInventory();
        const missingCommonProperties = readCommonTelemetryProperties()
            .filter(property => !Object.hasOwn(inventory.commonProperties, property));

        assert.deepStrictEqual(missingCommonProperties, []);
    });

    test('declares common properties added by VS Code and the telemetry reporter', () => {
        const inventory = readTelemetryInventory();
        const missingCommonProperties = platformCommonTelemetryProperties
            .filter(property => !Object.hasOwn(inventory.commonProperties, property));

        assert.deepStrictEqual(missingCommonProperties, []);
    });

    test('does not add telemetry properties that look like free-form text without an explicit inventory review', () => {
        const suspiciousRegistryEntries = readTelemetryRegistryEvents()
            .flatMap(event => event.entries
                .filter(entry => freeformPropertyNamePattern.test(entry))
                .map(entry => `${event.name}.${entry}`));

        assert.deepStrictEqual(suspiciousRegistryEntries, []);
    });

    test('documents bounded resource debug strategy telemetry', () => {
        const inventory = readTelemetryInventory();
        const inconsistencies = resourceDebugTelemetryPropertyExpectations.flatMap(expectation => {
            const inventoryProperty = inventory.events[expectation.eventName]?.[expectation.propertyName] as { comment?: unknown } | undefined;
            const actualValues = readResourceDebugTelemetryPropertyValues(expectation.interfaceName, expectation.propertyName);
            const actualComment = inventoryProperty?.comment;

            return actualComment === expectation.comment &&
                JSON.stringify(actualValues) === JSON.stringify(expectation.values)
                ? []
                : [{
                    eventName: expectation.eventName,
                    propertyName: expectation.propertyName,
                    expectedValues: expectation.values,
                    actualValues,
                    expectedComment: expectation.comment,
                    actualComment,
                }];
        });

        assert.deepStrictEqual(inconsistencies, []);
    });
});
