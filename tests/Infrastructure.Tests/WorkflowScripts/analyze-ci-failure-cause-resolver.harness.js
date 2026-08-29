const fs = require('node:fs/promises');
const resolver = require('../../../.github/workflows/analyze-ci-failure-cause-resolver.js');

async function main() {
    const inputPath = process.argv[2];
    if (!inputPath) {
        throw new Error('Expected the input payload file path as the first argument.');
    }

    const request = JSON.parse(await fs.readFile(inputPath, 'utf8'));
    if (request.operation !== 'resolveCauses') {
        throw new Error(`Unsupported operation '${request.operation}'.`);
    }

    const result = resolver.resolveCauses(request.payload ?? {});
    process.stdout.write(JSON.stringify({ result }));
}

main().catch(error => {
    process.stderr.write(`${error.stack ?? error}\n`);
    process.exitCode = 1;
});
