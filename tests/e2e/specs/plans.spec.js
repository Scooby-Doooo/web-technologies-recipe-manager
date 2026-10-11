import { readFileSync } from 'node:fs';
import { test, expect } from '../fixtures.js';
import { kitchenUser } from '../helpers.js';

const recipe = (categoryId, title) => ({ title, instructions: 'Cook.', cookingTimeMinutes: 10, servings: 1, categoryId });

test('[B6-01] the export page is a Premium feature that follows the kitchen plan', async ({ browser, app }) => {
    const user = await kitchenUser(browser, app, 'plan');
    try {
        const { page, api } = user;
        const [category] = await (await api.get('/api/categories')).json();
        expect((await api.post('/api/recipes', recipe(category.id, '=SUM(A1) "quoted" рецепт'))).status()).toBe(201);

        await page.goto('/Account');
        await expect(page.locator('#kitchen-plan')).toHaveText('Free');
        await expect(page.getByRole('link', { name: 'Export', exact: true })).toHaveCount(0);
        const blocked = await api.get('/Export');
        expect(blocked.status()).toBe(403);
        expect(await blocked.text()).toContain('This page is part of the Premium plan.');
        expect((await api.get('/Export/Json')).status()).toBe(403);

        await page.getByRole('button', { name: 'Switch to Premium', exact: true }).click();
        await expect(page.getByRole('status')).toContainText('is now on the Premium plan');
        await expect(page.locator('#kitchen-plan')).toHaveText('Premium');
        await page.getByRole('link', { name: 'Export', exact: true }).click();
        await expect(page.getByRole('heading', { name: 'Export recipes', exact: true })).toBeVisible();

        const download = page.waitForEvent('download');
        await page.getByRole('link', { name: 'Download JSON', exact: true }).click();
        const json = JSON.parse(readFileSync(await (await download).path(), 'utf8'));
        expect(json).toHaveLength(1);
        expect(json[0]).toMatchObject({ title: '=SUM(A1) "quoted" рецепт', category: category.name, ingredients: [] });

        const csv = await api.get('/Export/Csv');
        expect(csv.headers()['content-type']).toContain('text/csv');
        const csvText = (await csv.body()).toString('utf8');
        expect(csvText.charCodeAt(0), 'UTF-8 BOM for Excel').toBe(0xFEFF);
        // A leading "=" is neutralised so a spreadsheet does not run the title as a formula.
        expect(csvText).toContain(`"'=SUM(A1) ""quoted"" рецепт","${category.name}",10,1`);

        await page.goto('/Account');
        await page.getByRole('button', { name: 'Switch to Free', exact: true }).click();
        await expect(page.locator('#kitchen-plan')).toHaveText('Free');
        expect((await api.get('/Export/Csv')).status()).toBe(403);
    } finally { await user.context.close(); }
});

test('[B6-02] the Free plan caps recipes per kitchen and Premium lifts the cap', async ({ browser, app }) => {
    const user = await kitchenUser(browser, app, 'limit');
    try {
        const { page, api } = user;
        const [category] = await (await api.get('/api/categories')).json();
        for (let n = 1; n <= 3; n++) {
            expect((await api.post('/api/recipes', recipe(category.id, `QA2 limit recipe ${n}`))).status()).toBe(201);
        }
        const rejected = await api.post('/api/recipes', recipe(category.id, 'QA2 limit recipe 4'));
        expect(rejected.status()).toBe(403);
        expect((await rejected.json()).title).toContain('The Free plan allows 3 recipes per kitchen');

        await page.goto('/Recipes/Create');
        await expect(page.getByRole('alert')).toContainText('The Free plan allows 3 recipes per kitchen');
        await expect(page.getByRole('button', { name: 'Save', exact: true })).toHaveCount(0);

        await page.goto('/Account');
        await page.getByRole('button', { name: 'Switch to Premium', exact: true }).click();
        expect((await api.post('/api/recipes', recipe(category.id, 'QA2 limit recipe 4'))).status()).toBe(201);
        await page.goto('/Recipes/Create');
        await expect(page.getByRole('button', { name: 'Save', exact: true })).toBeVisible();
    } finally { await user.context.close(); }
});

test('[B6-03] the public demo kitchen stays on Premium', async ({ page }) => {
    await page.goto('/Account');
    await expect(page.locator('#kitchen-plan')).toHaveText('Premium');
    await expect(page.getByRole('button', { name: /^Switch to/ })).toHaveCount(0);
    // Even a crafted form post with a valid antiforgery token cannot downgrade it.
    const token = await page.locator('main input[name="__RequestVerificationToken"]').first().inputValue();
    const response = await page.request.post('/Account/ChangePlan', { form: { plan: 'Free', __RequestVerificationToken: token } });
    expect(response.status()).toBe(400);
});
