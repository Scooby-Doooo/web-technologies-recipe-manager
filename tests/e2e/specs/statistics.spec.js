import { test, expect } from '../fixtures.js';

test('[REG-01] F1 charts reflect real recipes and remain usable on mobile', async ({ page, data }) => {
    await data.recipe({ title: 'QA2 statistics recipe', cookingTimeMinutes: 20 });
    for (const [width, height] of [[1280, 900], [375, 812]]) {
        await page.setViewportSize({ width, height });
        await page.goto('/Statistics');
        await expect(page.locator('#statistics-total')).toHaveText('1');
        await expect(page.getByRole('img', { name: 'Recipes by category' })).toBeVisible();
        await expect(page.getByRole('img', { name: 'Cooking time', exact: true })).toBeVisible();
        const data = await page.evaluate(() => ({
            category: Chart.getChart('category-chart').data.datasets[0].data,
            time: Chart.getChart('time-chart').data.datasets[0].data
        }));
        expect(data.category.reduce((sum, count) => sum + count, 0)).toBe(1);
        expect(data.time.reduce((sum, count) => sum + count, 0)).toBe(1);
        await expect(page.locator('#category-statistics')).toContainText('Breakfast');
        expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
    }
});
