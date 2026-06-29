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

// Telemetry events emit verbatim to the wire — the registry-declared name
// (e.g. `aspire/vscode/command/invoked`, `aspire/dashboard/operation`) is
// what appears in `extension/telemetry.json`. The extension routes every
// event through `sendDangerousTelemetryEvent` so VS Code's automatic
// `<extensionId>/` prefix is bypassed; the inventory file therefore stores
// the bare wire name with no additional prefix.
const telemetryEntityPrefix = '';

function readTelemetryInventory(): TelemetryInventory {
    const inventoryPath = path.resolve(__dirname, '../../telemetry.json');
    return JSON.parse(fs.readFileSync(inventoryPath, 'utf8')) as TelemetryInventory;
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

suite('extension/telemetry.json', () => {
    test('event entity names are lowercase to match VS Code telemetry ingestion', () => {
        const inventory = readTelemetryInventory();
        const mixedCaseEntityNames = Object.keys(inventory.events).filter(name => name !== name.toLowerCase());

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
});
