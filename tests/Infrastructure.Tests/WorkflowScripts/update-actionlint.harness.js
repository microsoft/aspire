// Test harness for .github/workflows/update-actionlint.js, driven by UpdateActionlintTests.cs.
//
// Usage: node update-actionlint.harness.js <request.json>
//   request.json: { "operation": "<name>", "payload": { ... } }
//
// Dispatches the operation to the updater's exported functions and prints a
// single JSON object to stdout: { "result": ... } on success or
// { "error": "<message>" } when the updater throws. The 'check' operation
// replaces the network and filesystem with in-memory fakes so tests can supply
// release metadata and archive bytes and assert on every fetch and write.

const fs = require('node:fs/promises');
const updater = require('../../../.github/workflows/update-actionlint.js');

async function main() {
    const inputPath = process.argv[2];
    if (!inputPath) {
        throw new Error('Expected the input payload file path as the first argument.');
    }

    const request = JSON.parse(await fs.readFile(inputPath, 'utf8'));
    let response;
    try {
        response = { result: await dispatch(request.operation, request.payload ?? {}) };
    } catch (error) {
        response = { error: error.message };
    }

    process.stdout.write(JSON.stringify(response));
}

async function dispatch(operation, payload) {
    switch (operation) {
        case 'readPin':
            return updater.readPin(payload.content);

        case 'applyPin':
            return updater.applyPin(payload.content, payload.version, payload.sha256);

        case 'compareVersions':
            return updater.compareVersions(payload.left, payload.right);

        case 'parseReleaseTag':
            return updater.parseReleaseTag(payload.tag);

        case 'formatStepOutputs':
            return updater.formatStepOutputs(payload.result);

        case 'selectLinuxAmd64Asset':
            return updater.selectLinuxAmd64Asset(payload.release, payload.version);

        case 'check': {
            // Network and filesystem fakes: the release JSON and archive bytes come
            // from the payload, and every fetch/write is recorded for assertions.
            const calls = [];
            const result = await updater.check({
                pinContent: payload.pinContent,
                outputDir: '/tmp/out',
                fetchJson: async url => {
                    calls.push(`json ${url}`);
                    return payload.release;
                },
                fetchBytes: async url => {
                    calls.push(`bytes ${url}`);
                    return Buffer.from(payload.archive, 'utf8');
                },
                writeFile: async filePath => {
                    calls.push(`write ${filePath}`);
                },
                log: () => {},
            });

            return { ...result, calls };
        }

        default:
            throw new Error(`Unsupported operation '${operation}'.`);
    }
}

main().catch(error => {
    process.stderr.write(`${error.stack ?? error}\n`);
    process.exitCode = 1;
});
