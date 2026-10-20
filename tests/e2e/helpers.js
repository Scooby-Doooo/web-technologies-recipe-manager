import { request as httpRequest } from 'node:http';
import { expect } from './fixtures.js';

export async function search(page, term) {
    await page.getByRole('combobox', { name: /^Category/ }).click();
    const input = page.getByRole('searchbox', { name: 'Search categories' });
    await input.fill('');
    await input.pressSequentially(term);
    return input;
}

export async function chooseCategory(page, term, name) {
    await search(page, term);
    await page.getByRole('option', { name, exact: true }).click();
}

export async function fillRecipe(page, title = 'QA2 browser recipe') {
    await page.getByLabel('Title', { exact: true }).fill(title);
    await page.getByLabel('Description', { exact: true }).fill('A recipe created by the QA2 browser test.');
    await page.getByLabel('Cooking time (minutes)', { exact: true }).fill('25');
    await page.getByLabel('Servings', { exact: true }).fill('2');
    await page.getByLabel('Instructions', { exact: true }).fill('Mix the ingredients. Cook and serve.');
}

export async function save(page, controller = 'Recipes') {
    await page.getByRole('button', { name: 'Save', exact: true }).click();
    await expect(page).toHaveURL(new RegExp(`/${controller}$`));
}

export function row(page, name) {
    return page.locator('tbody tr').filter({ has: page.getByRole('cell', { name, exact: true }) });
}

export async function recipeId(page, title) {
    const href = await row(page, title).getByRole('link', { name: 'Details', exact: true }).getAttribute('href');
    return Number(href.split('/').at(-1));
}

export const png = Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aOxoAAAAASUVORK5CYII=', 'base64');

// A browser context signed in as a user with an own kitchen (not the shared public one).
export async function kitchenUser(browser, app, name) {
    const context = await browser.newContext({ baseURL: app.baseURL });
    const email = `qa2-${name}-${Date.now()}@example.test`;
    const signIn = await context.request.post('/Account/TestSignIn', { params: { email, kitchen: 'own' } });
    expect(signIn.status()).toBe(204);
    const { requestToken, headerName } = await (await context.request.get('/api/antiforgery')).json();
    const headers = { [headerName]: requestToken };
    const api = {
        get: path => context.request.get(path),
        post: (path, data) => context.request.post(path, { headers, data }),
        put: (path, data) => context.request.put(path, { headers, data }),
        delete: path => context.request.delete(path, { headers })
    };
    return { context, email, api, page: await context.newPage() };
}

// Sends a raw WebSocket handshake and reports the status code the server answers with.
export function handshake(url, cookie, origin) {
    return new Promise((resolve, reject) => {
        const request = httpRequest(url, {
            headers: { Connection: 'Upgrade', Upgrade: 'websocket', 'Sec-WebSocket-Version': '13',
                'Sec-WebSocket-Key': 'dGhlIHNhbXBsZSBub25jZQ==', Origin: origin, Cookie: cookie }
        });
        request.on('upgrade', (response, socket) => { socket.destroy(); resolve(response.statusCode); });
        request.on('response', response => { response.resume(); resolve(response.statusCode); });
        request.on('error', reject);
        request.end();
    });
}
