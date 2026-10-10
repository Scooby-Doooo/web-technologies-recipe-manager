import { test, expect } from '../fixtures.js';

// A browser context signed in as a user with an own kitchen (not the shared public one).
async function kitchenUser(browser, app, name) {
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

async function withKitchens(browser, app, run) {
    const alice = await kitchenUser(browser, app, 'alice');
    const bob = await kitchenUser(browser, app, 'bob');
    try { await run(alice, bob); } finally {
        await alice.context.close();
        await bob.context.close();
    }
}

test('[B5-01] a new user gets an own kitchen with its own copy of the default categories', async ({ browser, app, data }) => {
    const publicRecipe = await data.recipe({ title: 'QA2 public kitchen recipe' });
    await withKitchens(browser, app, async alice => {
        await alice.page.goto('/Account');
        await expect(alice.page.getByRole('heading', { name: /^Kitchen: Test user qa2-alice-.*'s kitchen$/ })).toBeVisible();
        await expect(alice.page.locator('#invite-code')).toHaveText(/^[A-Z2-9]{10}$/);
        await expect(alice.page.locator('#kitchen-members li')).toHaveCount(1);

        const categories = await (await alice.api.get('/api/categories')).json();
        expect(categories.map(item => item.name).sort()).toEqual(['Breakfast', 'Dessert', 'Main Course', 'Salad']);
        expect(categories.every(item => item.id > 4), 'Copies, not the public rows 1-4').toBe(true);

        await alice.page.goto('/Recipes');
        await expect(alice.page.getByText(publicRecipe.title)).toHaveCount(0);
        expect((await alice.api.get('/Recipes/Details/' + publicRecipe.id)).status()).toBe(404);
    });
});

test('[B5-02] one kitchen cannot read or change another kitchen data in MVC, API, statistics or sitemap', async ({ browser, app, request }) => {
    await withKitchens(browser, app, async (alice, bob) => {
        const category = await (await alice.api.post('/api/categories', { name: 'QA2 Alice secret category' })).json();
        const created = await alice.api.post('/api/recipes', { title: 'QA2 Alice secret recipe', instructions: 'Hidden.',
            cookingTimeMinutes: 10, servings: 1, categoryId: category.id });
        expect(created.status()).toBe(201);
        const recipe = await created.json();

        await bob.page.goto('/Recipes');
        await expect(bob.page.getByText('QA2 Alice secret recipe')).toHaveCount(0);
        for (const path of [`/Recipes/Details/${recipe.id}`, `/Recipes/Edit/${recipe.id}`, `/Recipes/Delete/${recipe.id}`,
            `/Categories/Edit/${category.id}`]) {
            expect((await bob.api.get(path)).status(), path).toBe(404);
        }
        expect((await bob.api.get(`/api/recipes/${recipe.id}`)).status()).toBe(404);
        expect((await bob.api.put(`/api/categories/${category.id}`, { name: 'Taken over' })).status()).toBe(404);
        expect((await bob.api.delete(`/api/recipes/${recipe.id}`)).status()).toBe(404);
        // Bob cannot use Alice's category for his own recipe either.
        expect((await bob.api.post('/api/recipes', { title: 'QA2 Bob recipe', instructions: 'Try.',
            cookingTimeMinutes: 5, servings: 1, categoryId: category.id })).status()).toBe(400);
        const options = await (await bob.api.get('/Recipes/CategoryOptions?term=secret')).json();
        expect(options.results).toEqual([]);

        await bob.page.goto('/Statistics');
        await expect(bob.page.locator('#statistics-total')).toHaveText('0');
        await alice.page.goto('/Statistics');
        await expect(alice.page.locator('#statistics-total')).toHaveText('1');

        const anonymousList = await (await request.get('/api/recipes')).json();
        expect(anonymousList.some(item => item.id === recipe.id)).toBe(false);
        expect(await (await bob.api.get('/sitemap.xml')).text()).not.toContain(`/Recipes/Details/${recipe.id}<`);

        // Alice still sees and owns everything she created.
        expect((await alice.api.get(`/api/recipes/${recipe.id}`)).status()).toBe(200);
        expect((await alice.api.delete(`/api/recipes/${recipe.id}`)).status()).toBe(204);
    });
});

test('[B5-03] an invite code lets another user join the kitchen and see its recipes', async ({ browser, app }) => {
    await withKitchens(browser, app, async (alice, bob) => {
        const recipe = await (await alice.api.post('/api/recipes', { title: 'QA2 shared kitchen recipe', instructions: 'Share.',
            cookingTimeMinutes: 15, servings: 2, categoryId: (await (await alice.api.get('/api/categories')).json())[0].id })).json();
        await alice.page.goto('/Account');
        const code = await alice.page.locator('#invite-code').innerText();

        await bob.page.goto('/Account');
        await bob.page.getByLabel('Join another kitchen').fill('WRONGCODE1');
        await bob.page.getByRole('button', { name: 'Join', exact: true }).click();
        await expect(bob.page.getByRole('alert')).toHaveText('No kitchen has this invite code.');

        await bob.page.getByLabel('Join another kitchen').fill(code.toLowerCase());
        await bob.page.getByRole('button', { name: 'Join', exact: true }).click();
        await expect(bob.page.getByRole('status')).toContainText('You joined');
        await expect(bob.page.locator('#kitchen-members li')).toHaveCount(2);

        await bob.page.goto('/Recipes');
        await expect(bob.page.getByRole('cell', { name: 'QA2 shared kitchen recipe', exact: true })).toBeVisible();
        expect((await bob.api.get(`/Recipes/Details/${recipe.id}`)).status()).toBe(200);
    });
});

test('[B5-04] cached API responses are kept apart per kitchen', async ({ browser, app, request }) => {
    await withKitchens(browser, app, async (alice, bob) => {
        await alice.api.post('/api/categories', { name: 'QA2 Alice cached category' });
        const first = await alice.api.get('/api/categories?skip=0&limit=100');
        const again = await alice.api.get('/api/categories?skip=0&limit=100');
        expect(again.headers()['age'], 'The second identical request is served from cache').toBeDefined();
        expect((await first.json()).items.map(item => item.name)).toContain('QA2 Alice cached category');

        for (const client of [bob.api, request]) {
            const response = await client.get('/api/categories?skip=0&limit=100');
            expect((await response.json()).items.map(item => item.name)).not.toContain('QA2 Alice cached category');
        }
    });
});
