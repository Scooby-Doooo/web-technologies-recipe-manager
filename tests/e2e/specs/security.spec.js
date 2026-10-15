import { test, expect, signIn, startExtraServer } from '../fixtures.js';
import { kitchenUser, png } from '../helpers.js';

const noRedirect = { maxRedirects: 0 };

async function anonymousClient(playwright, baseURL, extraHTTPHeaders) {
    return playwright.request.newContext({ baseURL, extraHTTPHeaders });
}

// Every MVC page that changes data, and every API write. Anonymous callers must be stopped
// before the action runs: MVC redirects to sign-in, the API answers 401.
const protectedPages = ['Recipes', 'Categories', 'Ingredients'].flatMap(controller => [
    ['GET', `/${controller}/Create`], ['POST', `/${controller}/Create`],
    ['GET', `/${controller}/Edit/1`], ['POST', `/${controller}/Edit/1`],
    ['GET', `/${controller}/Delete/1`], ['POST', `/${controller}/Delete/1`]
]).concat([['GET', '/Recipes/CategoryOptions?term=Bre'], ['GET', '/Account'], ['GET', '/Export'],
    ['GET', '/Assistant'], ['POST', '/Assistant/Chat'],
    ['POST', '/Account/ApiToken'], ['POST', '/Account/ChangePlan'], ['POST', '/Account/JoinKitchen'], ['POST', '/Account/RenameKitchen']]);
const protectedApi = ['recipes', 'categories', 'ingredients'].flatMap(resource => [
    ['POST', `/api/${resource}`], ['PUT', `/api/${resource}/1`], ['POST', `/api/${resource}/1`], ['DELETE', `/api/${resource}/1`]
]).concat([['POST', '/api/recipes/1/image']]);
const publicPages = ['/', '/Recipes', '/Categories', '/Categories/Details/1', '/Ingredients',
    '/Ingredients/Details/1', '/Statistics', '/Account/Login', '/robots.txt', '/sitemap.xml',
    '/api/recipes', '/api/categories', '/api/categories/1', '/api/ingredients', '/api/ingredients/1', '/api/antiforgery'];

test('[QA3-01] authorization matrix: anonymous callers can read but every write is stopped', async ({ playwright, app, data }) => {
    const recipe = await data.recipe({ title: 'QA3 public recipe' });
    const client = await anonymousClient(playwright, app.baseURL);
    try {
        const { requestToken, headerName } = await (await client.get('/api/antiforgery')).json();
        for (const [method, url] of protectedPages) {
            const response = await client.fetch(url, { method, ...noRedirect });
            expect(response.status(), `${method} ${url}`).toBe(302);
            const location = new URL(response.headers().location, app.baseURL);
            expect(location.pathname, `${method} ${url} goes to sign-in`).toBe('/Account/Login');
            expect(location.searchParams.get('ReturnUrl')).toBe(url);
        }
        for (const [method, url] of protectedApi) {
            // The image endpoint only accepts multipart; JSON would be refused with 415 before authorization.
            const body = url.endsWith('/image')
                ? { multipart: { imageFile: { name: 'qa3.png', mimeType: 'image/png', buffer: png } } }
                : { data: { name: 'QA3' } };
            const response = await client.fetch(url, { method, headers: { [headerName]: requestToken }, ...body, ...noRedirect });
            expect(response.status(), `${method} ${url}`).toBe(401);
        }
        for (const url of [...publicPages, `/Recipes/Details/${recipe.id}`, `/api/recipes/${recipe.id}`]) {
            expect((await client.get(url, noRedirect)).status(), `GET ${url}`).toBe(200);
        }
    } finally { await client.dispose(); }
});

test('[QA3-02] sign-in never redirects to another site (open redirect)', async ({ page }) => {
    // The page fixture is signed in, so Login sends the user straight to returnUrl when it is local.
    for (const [returnUrl, expected] of [['/Recipes', '/Recipes'], ['https://evil.example/steal', '/'],
        ['//evil.example', '/'], ['/\\evil.example', '/'], ['javascript:alert(1)', '/']]) {
        const response = await page.request.get('/Account/Login', { params: { returnUrl }, ...noRedirect });
        expect(response.status(), returnUrl).toBe(302);
        expect(response.headers().location, returnUrl).toBe(expected);
    }
});

test('[QA3-03] the auth cookie is HttpOnly and SameSite, and a tampered cookie is rejected', async ({ playwright, app }) => {
    const client = await anonymousClient(playwright, app.baseURL);
    try {
        const signInResponse = await client.post('/Account/TestSignIn', { params: { email: 'qa3-cookie@example.test' } });
        const setCookie = signInResponse.headers()['set-cookie'];
        expect(setCookie).toMatch(/\.AspNetCore\.Cookies=/);
        expect(setCookie.toLowerCase()).toContain('httponly');
        expect(setCookie.toLowerCase()).toContain('samesite=lax');
        expect((await client.get('/Account', noRedirect)).status()).toBe(200);

        const { cookies } = await client.storageState();
        const auth = cookies.find(cookie => cookie.name === '.AspNetCore.Cookies');
        const tampered = auth.value.slice(0, -4) + (auth.value.endsWith('AAAA') ? 'BBBB' : 'AAAA');
        const forged = await anonymousClient(playwright, app.baseURL, { Cookie: `.AspNetCore.Cookies=${tampered}` });
        try {
            const response = await forged.get('/Account', noRedirect);
            expect(response.status()).toBe(302);
            expect(response.headers().location).toContain('/Account/Login');
        } finally { await forged.dispose(); }
    } finally { await client.dispose(); }
});

test('[QA3-04] account actions reject requests without an antiforgery token (CSRF)', async ({ browser, app }) => {
    const user = await kitchenUser(browser, app, 'csrf');
    try {
        const { page, context } = user;
        await page.goto('/Account');
        const name = await page.getByLabel('Kitchen name').inputValue();
        for (const [url, form] of [['/Account/Logout', {}], ['/Account/ApiToken', {}], ['/Account/ChangePlan', { plan: 'Premium' }],
            ['/Account/RenameKitchen', { name: 'Hijacked' }], ['/Account/JoinKitchen', { inviteCode: 'ANYCODE123' }]]) {
            expect((await context.request.post(url, { form, ...noRedirect })).status(), url).toBe(400);
        }
        await page.reload();
        await expect(page.getByLabel('Kitchen name')).toHaveValue(name);
        await expect(page.locator('#kitchen-plan')).toHaveText('Free');
        await expect(page.getByRole('link', { name: 'Sign in', exact: true })).toHaveCount(0);
    } finally { await user.context.close(); }
});

test('[QA3-05] bearer tokens: forged ones fail and a token cannot mint new tokens', async ({ page, playwright, app }) => {
    await page.goto('/Account');
    await page.getByRole('button', { name: 'Create API token', exact: true }).click();
    const token = (await page.getByLabel(/^Token, valid until/).inputValue()).trim();
    const flipped = token.slice(0, 20) + (token[20] === 'A' ? 'B' : 'A') + token.slice(21);

    for (const [value, expected] of [[token, 201], [flipped, 401], ['', 401]]) {
        const client = await anonymousClient(playwright, app.baseURL, { Authorization: 'Bearer ' + value });
        try {
            const { requestToken, headerName } = await (await client.get('/api/antiforgery')).json();
            const response = await client.post('/api/categories', { headers: { [headerName]: requestToken }, data: { name: 'QA3 token category' } });
            expect(response.status(), value === token ? 'valid token' : 'forged token').toBe(expected);
            if (expected === 201) {
                expect((await client.delete(`/api/categories/${(await response.json()).id}`, { headers: { [headerName]: requestToken } })).status()).toBe(204);
                // Creating tokens needs the browser cookie; a stolen token cannot extend itself.
                const minted = await client.post('/Account/ApiToken', noRedirect);
                expect(minted.status()).toBe(302);
            }
        } finally { await client.dispose(); }
    }
});

test('[QA3-06] an expired bearer token is rejected', async ({ app, playwright }) => {
    const server = await startExtraServer(app.directory, 'short-tokens', 'Development',
        { Authentication__EnableTestSignIn: 'true', Authentication__ApiTokenLifetime: '00:00:02' });
    const browserClient = await anonymousClient(playwright, server.baseURL);
    try {
        await signIn(browserClient, 'qa3-expiry@example.test');
        const accountPage = await (await browserClient.get('/Account')).text();
        expect(accountPage).toContain('valid for 0 minutes');
        const formToken = accountPage.match(/name="__RequestVerificationToken" type="hidden" value="([^"]+)"/)[1];
        const tokenPage = await (await browserClient.post('/Account/ApiToken', { form: { __RequestVerificationToken: formToken } })).text();
        const token = tokenPage.match(/<textarea id="api-token"[^>]*>([^<]+)<\/textarea>/)[1].trim();

        const api = await anonymousClient(playwright, server.baseURL, { Authorization: 'Bearer ' + token });
        try {
            const { requestToken, headerName } = await (await api.get('/api/antiforgery')).json();
            const headers = { [headerName]: requestToken };
            const created = await api.post('/api/categories', { headers, data: { name: 'QA3 before expiry' } });
            expect(created.status()).toBe(201);
            await new Promise(resolve => setTimeout(resolve, 2500));
            expect((await api.delete(`/api/categories/${(await created.json()).id}`, { headers })).status()).toBe(401);
        } finally { await api.dispose(); }
    } finally {
        await browserClient.dispose();
        await server.close();
    }
});

test('[QA3-07] Production has no test sign-in, even when the flag is set', async ({ app, playwright }) => {
    const server = await startExtraServer(app.directory, 'production', 'Production', { Authentication__EnableTestSignIn: 'true' });
    const client = await anonymousClient(playwright, server.baseURL);
    try {
        const response = await client.post('/Account/TestSignIn', { params: { email: 'attacker@example.test' }, ...noRedirect });
        expect(response.status()).not.toBe(204);
        expect(response.headers()['set-cookie'] ?? '').not.toContain('.AspNetCore.Cookies=');
        expect((await client.get('/Recipes/Create', noRedirect)).status()).toBe(302);
        expect(await (await client.get('/Account/Login')).text()).toContain('Google sign-in is not configured');
    } finally {
        await client.dispose();
        await server.close();
    }
});
