// Collect Dependabot PR coverage using the same helpers as the approval gate.
const fs = require('node:fs');
const { execFileSync } = require('node:child_process');
const m = require('./auto-sec.js');
const prs = JSON.parse(fs.readFileSync('.auto-sec/dependabot-prs-raw.json', 'utf8'));
const alerts = JSON.parse(fs.readFileSync('.auto-sec/alerts.json', 'utf8'));
const ok = new Set(['SUCCESS', 'SKIPPED', 'NEUTRAL']);
const headText = (path, sha) => {
    const encoded = path.split('/').map(encodeURIComponent).join('/');
    return execFileSync('gh', ['api', '-H', 'Accept: application/vnd.github.raw', 'repos/' + process.env.REPO + '/contents/' + encoded + '?ref=' + sha],
        { encoding: 'utf8', maxBuffer: 256 * 1024 * 1024, stdio: ['ignore', 'pipe', 'ignore'] });
};
const out = prs.map(pr => {
    const rollup = pr.statusCheckRollup ?? [];
    const state = c => c.conclusion || c.state || c.status || '';
    const ecosystem = m.ecosystemFromBranch(pr.headRefName);
    const updates = m.parseDependabotUpdates(pr.title, pr.body);
    // REST file entries carry { filename, status, previous_filename }.
    // Only added/removed statuses prove an absent side; even a 404 fetching
    // an expected file must fail collection, not erase Dependabot coverage.
    // https://docs.github.com/rest/pulls/pulls#list-pull-requests-files
    const changedFiles = JSON.parse(execFileSync('gh', ['api', '--paginate', '--slurp', 'repos/' + process.env.REPO + '/pulls/' + pr.number + '/files?per_page=100'],
        { encoding: 'utf8', maxBuffer: 256 * 1024 * 1024, stdio: ['ignore', 'pipe', 'ignore'] })).flat();
    const files = changedFiles.map(f => f.filename);
    const headContents = {};
    const baseContents = {};
    const versionChanges = [];
    for (const file of changedFiles.filter(f => m.isAllowedManifest(f.filename))) {
        const path = file.filename;
        if (!['added', 'removed', 'modified', 'renamed', 'changed', 'copied'].includes(file.status)
            || (file.status === 'renamed' && typeof file.previous_filename !== 'string')) {
            throw new Error('Unsupported Dependabot file status');
        }
        const basePath = file.status === 'renamed' ? file.previous_filename : path;
        baseContents[path] = file.status === 'added' ? '' : headText(basePath, pr.baseRefOid);
        if (file.status !== 'removed') {
            const text = headText(path, pr.headRefOid);
            headContents[path] = text;
            if (ecosystem && ecosystem !== 'actions') {
                versionChanges.push(...m.manifestVersionChanges(path, baseContents[path], text, ecosystem));
            }
        }
    }
    return {
        number: pr.number,
        title: pr.title,
        head_ref: pr.headRefName,
        head_sha: pr.headRefOid,
        draft: pr.isDraft,
        ecosystem,
        updates,
        files,
        covered_alerts: m.coveredAlerts(alerts, ecosystem, updates, versionChanges, headContents, baseContents),
        checks: { total: rollup.length, green: rollup.filter(c => ok.has(String(state(c)).toUpperCase())).length },
    };
});
fs.writeFileSync('.auto-sec/dependabot-prs.json', JSON.stringify(out, null, 2));
