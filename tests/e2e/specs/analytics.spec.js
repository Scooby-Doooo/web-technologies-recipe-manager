import { test, expect } from '../fixtures.js';
import { chooseCategory, fillRecipe, recipeId, save } from '../helpers.js';

// gtag() pushes its arguments object to dataLayer; keep only the custom events.
const events = page => page.evaluate(() => window.dataLayer
    .map(entry => Array.from(entry))
    .filter(([command]) => command === 'event')
    .map(([, name, params]) => ({ name, params })));

test('[C9-01] every page loads the Google tag and the recipe page sends view_recipe', async ({ page, data }) => {
    const recipe = await data.recipe({ title: 'QA2 analytics soup', cookingTimeMinutes: 35 });
    await page.goto('/Statistics');
    await expect(page.locator('script[src*="googletagmanager.com/gtag/js"]')).toHaveAttribute('src', /id=G-QA2TEST000$/);
    const config = await page.evaluate(() => window.dataLayer.map(entry => Array.from(entry)).find(([command]) => command === 'config'));
    expect(config).toEqual(['config', 'G-QA2TEST000', { debug_mode: true }]);
    expect(await events(page)).toEqual([]);

    await page.goto('/Recipes/Details/' + recipe.id);
    expect(await events(page)).toEqual([{ name: 'view_recipe',
        params: { recipe_id: recipe.id, recipe_category: 'Breakfast', cooking_time_minutes: 35 } }]);
});

test('[C9-02] creating a recipe through the form sends recipe_created once', async ({ page }) => {
    const title = 'QA2 analytics created recipe';
    await page.goto('/Recipes/Create');
    await fillRecipe(page, title);
    await chooseCategory(page, 'Sal', 'Salad');
    await save(page);
    const id = await recipeId(page, title);
    expect(await events(page)).toEqual([{ name: 'recipe_created', params: { recipe_id: id } }]);

    await page.reload();
    expect(await events(page), 'TempData is read once, so a reload does not repeat the event').toEqual([]);
    await page.goto(`/Recipes/Delete/${id}`);
    await page.getByRole('button', { name: 'Delete', exact: true }).click();
    await expect(page).toHaveURL(/\/Recipes$/);
});
