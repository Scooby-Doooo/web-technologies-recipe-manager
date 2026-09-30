import { spawnSync } from 'node:child_process';
import { rmSync } from 'node:fs';
import { fileURLToPath } from 'node:url';

const args = process.argv.slice(2);
const execute = !args.includes('--list') && !args.includes('--ui');
if (execute) {
    // Remove only these generated files so an unsuccessful run cannot expose earlier passing coverage.
    for (const name of ['results.json', 'functional-coverage.json']) rmSync(new URL('./test-results/' + name, import.meta.url), { force: true });
}
const cli = fileURLToPath(import.meta.resolve('@playwright/test/cli'));
const run = spawnSync(process.execPath, [cli, 'test', ...args], { stdio: 'inherit', windowsHide: true });
process.exitCode = run.status ?? 1;
if (run.error) throw run.error;
if (execute && run.status !== null) {
    const coverage = spawnSync(process.execPath, [fileURLToPath(new URL('./coverage.js', import.meta.url))], {
        stdio: 'inherit', windowsHide: true
    });
    if (run.status === 0) process.exitCode = coverage.status ?? 1;
    if (coverage.error) throw coverage.error;
}
