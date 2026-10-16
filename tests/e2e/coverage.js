import { readFileSync, writeFileSync } from 'node:fs';

const report = JSON.parse(readFileSync(new URL('./test-results/results.json', import.meta.url), 'utf8'));
const cases = [];
const routes = new Set();
const visit = suite => {
    for (const spec of suite.specs || []) {
        for (const test of spec.tests) {
            const result = test.results.at(-1);
            const passed = test.status === 'expected' && result?.status === 'passed';
            cases.push({ id: spec.title.match(/^\[([^\]]+)\]/)?.[1], title: spec.title, passed });
            if (!passed) continue;
            for (const attachment of result.attachments || []) {
                if (attachment.name !== 'route-observations' || !attachment.body) continue;
                const observations = JSON.parse(Buffer.from(attachment.body, 'base64').toString());
                for (const item of observations) {
                    if (item.status >= 200 && item.status < 400) routes.add(item.method + ' ' + item.path);
                }
            }
        }
    }
    for (const child of suite.suites || []) visit(child);
};
for (const suite of report.suites) visit(suite);

const expectedCases = [
    ...Array.from({ length: 9 }, (_, i) => 'C1-' + String(i + 1).padStart(2, '0')),
    'C1-10a', 'C1-10b', 'C1-10c',
    ...Array.from({ length: 10 }, (_, i) => 'F2-' + String(i + 1).padStart(2, '0')), 'REG-01',
    'SEO-01', 'SEO-02', 'C9-01', 'C9-02',
    'AUTH-01', 'AUTH-02', 'AUTH-03',
    'B5-01', 'B5-02', 'B5-03', 'B5-04',
    'B6-01', 'B6-02', 'B6-03',
    ...Array.from({ length: 7 }, (_, i) => 'QA3-0' + (i + 1)),
    'TG-01', 'AI-01', 'AI-02', 'C15-01', 'C15-02'
];
const expectedRoutes = [];
for (const controller of ['Recipes', 'Categories', 'Ingredients']) {
    expectedRoutes.push('GET /' + controller, 'GET /' + controller + '/Create');
    for (const action of ['Details', 'Edit', 'Delete']) expectedRoutes.push(`GET /${controller}/${action}/:id`);
    expectedRoutes.push(`POST /${controller}/Create`, `POST /${controller}/Edit/:id`, `POST /${controller}/Delete/:id`);
}
expectedRoutes.push('GET /Recipes/CategoryOptions');
const passed = new Set(cases.filter(item => item.passed).map(item => item.id));
const missingCases = expectedCases.filter(id => !passed.has(id));
const missingRoutes = expectedRoutes.filter(route => !routes.has(route));
const result = {
    startedAt: report.stats.startTime,
    plannedScenarioCount: expectedCases.length,
    passedScenarioCount: expectedCases.length - missingCases.length,
    scopedRouteCount: expectedRoutes.length,
    observedSuccessfulRouteCount: expectedRoutes.length - missingRoutes.length,
    missingCases, missingRoutes, cases,
    routes: expectedRoutes.map(route => ({ route, observed: routes.has(route) })),
    interpretation: 'Coverage of the documented C1/F2 scenarios and MVC method/route pairs observed in passing browser tests. Not statement, branch or total application coverage.'
};
writeFileSync(new URL('./test-results/functional-coverage.json', import.meta.url), JSON.stringify(result, null, 2));
console.log(`QA2 functional coverage: ${result.passedScenarioCount}/${result.plannedScenarioCount} documented scenarios; ${result.observedSuccessfulRouteCount}/${result.scopedRouteCount} C1/F2 method/route pairs.`);
if (report.errors.length) process.exitCode = 1;
