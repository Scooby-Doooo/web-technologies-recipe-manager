import { test, expect } from '../fixtures.js';
import { kitchenUser } from '../helpers.js';

// "Butter 1.5 tbsp" or "Butter 1,500 tbsp": the amount is shown in the server culture.
const line = (name, number, unit) =>
    new RegExp(`^${name}\\s+${String(number).replace('.', '[.,]')}(?:[.,]?0*)?\\s+${unit}$`);

async function importForm(user) {
    await user.page.goto('/Import');
    const token = await user.page.locator('#import-preview-form input[name="__RequestVerificationToken"]').inputValue();
    return id => user.context.request.post('/Import/Meal/' + id, { form: { __RequestVerificationToken: token }, maxRedirects: 0 });
}

test('[C8-01] live search with filters, preview and import of a TheMealDB meal', async ({ browser, app }) => {
    const user = await kitchenUser(browser, app, 'import');
    const { page } = user;
    try {
        await page.goto('/Import');
        const status = page.locator('#import-status');
        const cards = page.locator('#import-results .card');
        const term = page.getByLabel('Meal name');
        await term.fill('qa');
        await expect(status).toHaveText('Found 3 meals.');
        await expect(cards.locator('.card-title')).toHaveText(['QA Chicken Curry', 'QA Garlic Salmon', 'QA Lemon Tart']);
        await page.getByLabel('Category').selectOption('Seafood');
        await expect(cards).toHaveCount(1);
        await expect(page).toHaveURL(/\/Import\?term=qa&category=Seafood$/);
        // Without a name the category and the ingredient go to filter.php and are intersected.
        await term.fill('');
        await expect(cards.locator('.card-title')).toHaveText(['QA Garlic Salmon']);
        await page.getByLabel('Category').selectOption('');
        await page.getByLabel('Main ingredient').fill('garlic');
        await page.getByLabel('Main ingredient').press('Enter');
        await expect(status).toHaveText('Found 2 meals.');
        await page.getByLabel('Category').selectOption('Chicken');
        await expect(cards.locator('.card-title')).toHaveText(['QA Chicken Curry']);
        await page.getByLabel('Main ingredient').fill('');
        await page.getByLabel('Category').selectOption('');
        await expect(status).toHaveText(/^Type at least 2 letters/);
        // The same search again is answered from the cache.
        await term.fill('qa');
        await expect(cards).toHaveCount(3);
        expect(app.mealDb.requests.filter(request => request === '/api/json/v1/1/search.php?s=qa')).toHaveLength(1);

        await page.getByRole('button', { name: 'Preview QA Garlic Salmon' }).click();
        const preview = page.getByRole('dialog');
        await expect(preview.getByRole('heading', { name: 'QA Garlic Salmon' })).toBeVisible();
        await expect(preview.locator('#import-preview-meta')).toHaveText('Seafood · British');
        await expect(preview.locator('#import-preview-ingredients li').nth(2)).toHaveText(/Butter\s*1 1\/2 tbsp/);
        await expect(preview.locator('#import-preview-instructions')).toContainText('Fry the salmon with garlic.');
        await expect(preview.getByRole('link', { name: 'Video on YouTube' })).toHaveAttribute('href', 'https://www.youtube.com/watch?v=qa');
        await preview.getByRole('button', { name: 'Import to my kitchen' }).click();

        await expect(page).toHaveURL(/\/Recipes\/Edit\/\d+$/);
        await expect(page.locator('.alert-info')).toContainText('imported from TheMealDB');
        await expect(page.getByLabel('Cooking time (minutes)')).toHaveValue('30');
        await expect(page.getByLabel('Servings')).toHaveValue('4');
        await expect(page.locator('#CategoryId option:checked')).toHaveText('Seafood');
        const id = page.url().match(/(\d+)$/)[1];
        await page.goto('/Recipes/Details/' + id);
        await expect(page.locator('.recipe-description')).toHaveText('British cuisine. Imported from TheMealDB.');
        const lines = (await page.locator('.recipe-ingredients li').allTextContents()).map(text => text.replace(/\s+/g, ' ').trim());
        for (const expected of [line('Salmon', 2, 'fillets'), line('garlic', 5, 'cloves'), line('Butter', 1.5, 'tbsp'),
            line('Lemon', 0.5, 'pcs'), line('Salt', 1, 'To taste'), line('Dill', 2, 'sprigs'), line('Milk', 200, 'ml')]) {
            expect(lines.some(text => expected.test(text)), `${expected} in ${lines.join('; ')}`).toBe(true);
        }
        expect(lines).toHaveLength(7);
        const photo = page.getByRole('img', { name: 'Image for QA Garlic Salmon', exact: true });
        await expect(photo).toHaveAttribute('src', /^\/uploads\/recipes\/[0-9a-f]{32}\.png$/);
        expect(await photo.evaluate(element => element.complete && element.naturalWidth)).toBe(1);
        // "Milk" already existed in the kitchen, so the import reused it.
        const ingredients = await (await user.api.get('/api/ingredients')).json();
        expect(ingredients.filter(item => item.name.toLowerCase() === 'milk')).toHaveLength(1);

        await page.goto('/Import?term=salmon');
        await expect(cards.getByText('In your kitchen')).toBeVisible();
        await page.getByRole('button', { name: 'Preview QA Garlic Salmon' }).click();
        await expect(preview.getByRole('link', { name: 'Open recipe in your kitchen' })).toHaveAttribute('href', '/Recipes/Details/' + id);
        await expect(preview.getByRole('button', { name: 'Import to my kitchen' })).toBeHidden();
        const again = await (await importForm(user))(90001);
        expect(again.status()).toBe(302);
        expect(again.headers().location).toBe('/Recipes/Details/' + id);
    } finally {
        await user.context.close();
    }
});

test('[C8-02] import takes pictures only from TheMealDB, keeps plan limits and reports outages', async ({ browser, app }) => {
    const user = await kitchenUser(browser, app, 'import-safety');
    try {
        const post = await importForm(user);
        const edit = /^\/Recipes\/Edit\/\d+$/;
        const curry = await post(90002);
        expect(curry.headers().location).toMatch(edit);
        const tart = await post(90003);
        expect(tart.headers().location).toMatch(edit);
        const recipes = await (await user.api.get('/api/recipes')).json();
        expect(recipes.map(recipe => [recipe.title, recipe.imagePath])).toEqual(
            expect.arrayContaining([['QA Chicken Curry', null], ['QA Lemon Tart', null]]));
        // The curry picture is on another host and is never requested; the tart "picture" is HTML.
        expect(app.mealDb.requests).not.toContain('/images/media/meals/qa-curry.png');
        expect(app.mealDb.requests).toContain('/images/media/meals/qa-tart.png');
        const categories = await (await user.api.get('/api/categories')).json();
        expect(categories.filter(item => item.name.toLowerCase() === 'dessert')).toHaveLength(1);
        const ingredients = await (await user.api.get('/api/ingredients')).json();
        expect(ingredients.filter(item => item.name.toLowerCase() === 'flour')).toHaveLength(1);

        expect((await post(99999)).status()).toBe(404);
        expect((await user.api.get('/Import/Search?term=' + 'x'.repeat(101))).status()).toBe(400);

        // The test server allows 3 recipes on the Free plan.
        expect((await user.api.post('/api/recipes', { title: 'QA third recipe', instructions: 'Cook.', cookingTimeMinutes: 5,
            servings: 1, categoryId: categories[0].id })).status()).toBe(201);
        const limited = await post(90001);
        expect(limited.status()).toBe(403);
        expect(await limited.text()).toContain('The Free plan allows 3 recipes');

        app.mealDb.state.failing = true;
        await user.page.getByLabel('Meal name').fill('outage');
        await expect(user.page.locator('#import-status')).toHaveText('TheMealDB is not available right now. Please try again later.');
        expect((await user.api.get('/Import/Search?term=outage')).status()).toBe(502);
    } finally {
        app.mealDb.state.failing = false;
        await user.context.close();
    }
});
