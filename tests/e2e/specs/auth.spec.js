import { test, expect, signIn } from '../fixtures.js';

// The page fixture is already signed in, so anonymous checks use a separate browser context.
async function anonymous(browser, app, run) {
    const context = await browser.newContext({ baseURL: app.baseURL });
    try { await run(context); } finally { await context.close(); }
}

test('[AUTH-01] anonymous visitors can browse, but editing pages redirect to sign-in', async ({ browser, app }) => {
    await anonymous(browser, app, async context => {
        const page = await context.newPage();
        await page.goto('/Recipes');
        await expect(page.getByRole('heading', { name: 'Recipes', exact: true })).toBeVisible();
        await expect(page.getByRole('link', { name: 'Sign in', exact: true })).toBeVisible();

        await page.goto('/Categories/Create');
        await expect(page).toHaveURL(/\/Account\/Login\?ReturnUrl=%2FCategories%2FCreate$/);
        await expect(page.getByRole('heading', { name: 'Sign in', exact: true })).toBeVisible();
        // The test server has no Google client id, so the page explains how to configure it.
        await expect(page.getByRole('alert')).toContainText('Authentication:Google:ClientId');
    });
});

test('[AUTH-02] the first sign-in registers a local account shown on the account page', async ({ browser, app }) => {
    await anonymous(browser, app, async context => {
        const email = `qa2-${Date.now()}@example.test`;
        await signIn(context.request, email);
        const page = await context.newPage();
        await page.goto('/Account');
        await expect(page.getByRole('heading', { name: 'Your account', exact: true })).toBeVisible();
        await expect(page.locator('dl')).toContainText(email);
        await expect(page.locator('dl')).toContainText('Test user ' + email);

        await page.getByRole('button', { name: 'Sign out', exact: true }).click();
        await expect(page).toHaveURL(/\/$/);
        await expect(page.getByRole('link', { name: 'Sign in', exact: true })).toBeVisible();
        await page.goto('/Account');
        await expect(page).toHaveURL(/\/Account\/Login/);
    });
});

test('[AUTH-03] the API rejects anonymous writes and accepts a bearer token from the account page', async ({ browser, app, page, playwright }) => {
    await anonymous(browser, app, async context => {
        const { requestToken, headerName } = await (await context.request.get('/api/antiforgery')).json();
        const rejected = await context.request.post('/api/categories', { headers: { [headerName]: requestToken }, data: { name: 'QA2 anonymous' } });
        expect(rejected.status()).toBe(401);
    });

    await page.goto('/Account');
    await page.getByRole('button', { name: 'Create API token', exact: true }).click();
    const token = (await page.getByLabel(/^Token, valid until/).inputValue()).trim();
    expect(token.length).toBeGreaterThan(50);

    // A client without any browser cookies, like the MCP server.
    const client = await playwright.request.newContext({ baseURL: app.baseURL, extraHTTPHeaders: { Authorization: 'Bearer ' + token } });
    try {
        const { requestToken, headerName } = await (await client.get('/api/antiforgery')).json();
        const created = await client.post('/api/categories', { headers: { [headerName]: requestToken }, data: { name: 'QA2 bearer category' } });
        expect(created.status()).toBe(201);
        const { id } = await created.json();
        expect((await client.delete(`/api/categories/${id}`, { headers: { [headerName]: requestToken } })).status()).toBe(204);
    } finally { await client.dispose(); }

    const forged = await playwright.request.newContext({ baseURL: app.baseURL, extraHTTPHeaders: { Authorization: 'Bearer not-a-real-token' } });
    try {
        expect((await forged.delete('/api/categories/1')).status()).toBe(401);
    } finally { await forged.dispose(); }
});
