// Test harness for .github/workflows/update-dependencies-version-properties.js,
// driven by UpdateDependenciesVersionPropertiesTests.cs.
//
// Usage: node update-dependencies-version-properties.harness.js <request.json>
//   request.json: the input object for restoreVersionProperties
//
// Prints a single JSON object to stdout: { "result": ... } on success or
// { "error": "<message>" } when the script throws.

const fs = require('node:fs/promises');
const { restoreVersionProperties } = require('../../../.github/workflows/update-dependencies-version-properties.js');

async function main() {
    const inputPath = process.argv[2];
    if (!inputPath) {
        throw new Error('Expected the input payload file path as the first argument.');
    }

    const request = JSON.parse(await fs.readFile(inputPath, 'utf8'));
    let response;
    try {
        response = { result: restoreVersionProperties(request) };
    } catch (error) {
        response = { error: error.message };
    }

    process.stdout.write(JSON.stringify(response));
}

main().catch(error => {
    process.stderr.write(`${error.stack ?? error}\n`);
    process.exitCode = 1;
});
