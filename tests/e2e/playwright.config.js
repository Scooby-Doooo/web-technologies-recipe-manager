import { defineConfig } from '@playwright/test';

export default defineConfig({
    testDir: './specs',
    fullyParallel: false,
    workers: 1,
    retries: 0,
    forbidOnly: true,
    timeout: 30_000,
    expect: { timeout: 5_000 },
    outputDir: './test-results/artifacts',
    reporter: [['list'], ['json', { outputFile: './test-results/results.json' }]],
    use: {
        browserName: 'chromium',
        channel: 'chrome',
        viewport: { width: 1280, height: 900 },
        screenshot: 'only-on-failure',
        trace: 'off' // traces could store tokens and cookies
    }
});
