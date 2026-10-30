import { test, expect } from '../fixtures.js';
import { kitchenUser, png } from '../helpers.js';

test('[F5-01] The React SPA lists, searches, creates, edits and deletes recipes through the JSON API', async ({ browser, app }) => {
    const user = await kitchenUser(browser, app, 'spa');
    const { page } = user;
    const errors = [];
    page.on('pageerror', error => errors.push(error.message));
    // Only the JSON API is called while the SPA works, never an MVC page.
    const requests = [];
    page.on('request', request => requests.push(new URL(request.url()).pathname));
    try {
        // The test Free plan allows 3 recipes; paging needs more.
        await page.goto('/Account');
        await page.getByRole('button', { name: 'Switch to Premium', exact: true }).click();
        await expect(page.locator('#kitchen-plan')).toHaveText('Premium');
        const [category] = await (await user.api.get('/api/categories')).json();

        await page.goto('/spa/');
        await expect(page.locator('#spa-account')).toContainText('Test user qa2-spa-');
        requests.length = 0;

        // The server checks the form again; its messages appear next to the fields.
        await page.getByRole('link', { name: 'New recipe' }).click();
        await expect(page).toHaveURL(/\/spa\/recipes\/new$/);
        await page.getByRole('button', { name: 'Save' }).click();
        await expect(page.getByLabel('Title', { exact: true })).toHaveClass(/is-invalid/);
        await expect(page.locator('#spa-title-error')).toContainText('required');

        await page.getByLabel('Title', { exact: true }).fill('SPA garlic soup');
        await page.getByLabel('Category').selectOption(String(category.id));
        await page.getByLabel('Description').fill('Made in React.');
        await page.getByLabel('Cooking time (minutes)').fill('20');
        await page.getByLabel('Servings').fill('2');
        await page.getByLabel('Instructions').fill('Fry the garlic.\nAdd the stock.');
        await page.getByLabel('Photo (optional)').setInputFiles({ name: 'soup.png', mimeType: 'image/png', buffer: png });
        await page.getByRole('button', { name: 'Save' }).click();

        await expect(page).toHaveURL(/\/spa\/recipes\/\d+$/);
        const id = Number(page.url().split('/').at(-1));
        await expect(page.getByRole('heading', { level: 1 })).toHaveText('SPA garlic soup');
        await expect(page.getByText('20 min · 2 servings')).toBeVisible();
        await expect(page.getByRole('img', { name: 'Photo of SPA garlic soup' })).toHaveAttribute('src', /^\/uploads\/recipes\/[0-9a-f]{32}\.png$/);

        // A deep link is answered with the SPA, whose router opens the recipe.
        await page.reload();
        await expect(page.getByRole('heading', { level: 1 })).toHaveText('SPA garlic soup');

        await page.getByRole('link', { name: 'Edit' }).click();
        await expect(page.getByLabel('Title', { exact: true })).toHaveValue('SPA garlic soup');
        await page.getByLabel('Title', { exact: true }).fill('SPA garlic soup deluxe');
        await page.getByRole('button', { name: 'Save' }).click();
        await expect(page.getByRole('heading', { level: 1 })).toHaveText('SPA garlic soup deluxe');
        expect((await (await user.api.get('/api/recipes/' + id)).json()).title).toBe('SPA garlic soup deluxe');

        // Pages of 12 through nextLink, then full-text search kept in the address.
        for (let i = 1; i <= 12; i++) {
            expect((await user.api.post('/api/recipes', { title: `SPA tomato pasta ${String(i).padStart(2, '0')}`,
                instructions: 'Boil the pasta.', cookingTimeMinutes: 40, servings: 2, categoryId: category.id })).status()).toBe(201);
        }
        await page.getByRole('link', { name: 'Recipes', exact: true }).click();
        const cards = page.locator('article.card');
        await expect(cards).toHaveCount(12);
        await page.getByRole('button', { name: 'Load more' }).click();
        await expect(cards).toHaveCount(13);
        await expect(page.getByRole('button', { name: 'Load more' })).toHaveCount(0);

        await page.getByLabel('Search recipes').fill('garlic');
        await expect(page).toHaveURL(/\?q=garlic$/);
        await expect(page.getByRole('status')).toHaveText('1 recipe found');
        await expect(cards).toHaveCount(1);
        await page.getByLabel('Search recipes').fill('pasta');
        await expect(page.getByRole('status')).toHaveText('12 recipes found');
        await page.getByLabel('Cooking time').selectOption('30');
        await expect(page).toHaveURL(/maxMinutes=30/);
        await expect(page.getByRole('status')).toHaveText('0 recipes found');
        await page.getByLabel('Search recipes').fill('title:(');
        await expect(page.getByRole('alert')).toContainText('The search query is not valid');

        await page.goto('/spa/?q=garlic');
        await expect(page.getByLabel('Search recipes')).toHaveValue('garlic');
        await cards.getByRole('link', { name: 'SPA garlic soup deluxe' }).click();
        page.once('dialog', dialog => dialog.accept());
        await page.getByRole('button', { name: 'Delete' }).click();
        await expect(page).toHaveURL(/\/spa\/?$/);
        expect((await user.api.get('/api/recipes/' + id)).status()).toBe(404);

        const pages = requests.filter(path => !path.startsWith('/api/') && !path.startsWith('/spa/') && !path.startsWith('/uploads/'));
        expect(pages, 'The SPA calls only the JSON API').toEqual([]);

        // Anonymous visitors read but are asked to sign in before changing anything.
        const anonymous = await browser.newPage({ baseURL: app.baseURL });
        await anonymous.goto('/spa/');
        await expect(anonymous.getByRole('link', { name: 'Sign in' })).toHaveAttribute('href', '/Account/Login?returnUrl=%2Fspa%2F');
        await expect(anonymous.getByRole('link', { name: 'New recipe' })).toHaveCount(0);
        await anonymous.goto('/spa/recipes/new');
        await expect(anonymous.getByText('Please sign in to add recipes.')).toBeVisible();
        await anonymous.goto('/spa/recipes/999999');
        await expect(anonymous.getByRole('heading', { name: 'Not found' })).toBeVisible();
        await anonymous.close();

        expect(errors).toEqual([]);
    } finally {
        await user.context.close();
    }
});
