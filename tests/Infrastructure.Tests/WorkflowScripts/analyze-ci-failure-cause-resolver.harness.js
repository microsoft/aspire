const fs = require('node:fs/promises');
const resolver = require('../../../.github/workflows/analyze-ci-failure-cause-resolver.js');

async function main() {
    const inputPath = process.argv[2];
    if (!inputPath) {
        throw new Error('Expected the input payload file path as the first argument.');
    }

    const request = JSON.parse(await fs.readFile(inputPath, 'utf8'));
    let result;
    switch (request.operation) {
        case 'resolveCauses':
            result = resolver.resolveCauses(request.payload ?? {});
            break;
        case 'validateCauseJobAttribution':
            result = resolver.validateCauseJobAttribution(
                request.payload?.analysis,
                request.payload?.causes,
                request.payload?.trustedFailedJobs);
            break;
        default:
            throw new Error(`Unsupported operation '${request.operation}'.`);
    }

    process.stdout.write(JSON.stringify({ result }));
}

main().catch(error => {
    process.stderr.write(`${error.stack ?? error}\n`);
    process.exitCode = 1;
});
