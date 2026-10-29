import { test, expect } from '../fixtures.js';
import { kitchenUser } from '../helpers.js';

test('[B4-01] Lucene search finds recipes by any field, ranks them, pages them and follows changes', async ({ browser, app, data }) => {
    const user = await kitchenUser(browser, app, 'search');
    const other = await kitchenUser(browser, app, 'search-other');
    try {
        // The test Free plan allows 3 recipes; this kitchen needs 4.
        await user.page.goto('/Account');
        await user.page.getByRole('button', { name: 'Switch to Premium', exact: true }).click();
        await expect(user.page.locator('#kitchen-plan')).toHaveText('Premium');
        const [category] = await (await user.api.get('/api/categories')).json();
        const recipe = async (title, instructions, cookingTimeMinutes, description = null) => {
            const response = await user.api.post('/api/recipes', { title, description, instructions,
                cookingTimeMinutes, servings: 2, categoryId: category.id });
            expect(response.status()).toBe(201);
            return response.json();
        };
        const soup = await recipe('Tomato soup', 'Simmer the tomatoes with garlic for twenty minutes.', 30);
        const bread = await recipe('Garlic bread', 'Spread the butter on the bread and bake.', 15, 'Crunchy and quick.');
        const pancakes = await recipe('Pancakes', 'Whisk everything and fry.', 20);
        await recipe('Борщ', 'Варити буряк і капусту.', 90);
        const buttermilk = await (await user.api.post('/api/ingredients', { name: 'Buttermilk' })).json();
        await data.link(pancakes.id, buttermilk.id); // Test setup: MVC has no ingredient relationship editor.

        const search = async (params, client = user) => {
            const response = await client.api.get('/api/recipes/search?' + new URLSearchParams(params));
            expect(response.status(), JSON.stringify(params)).toBe(200);
            return response.json();
        };
        const titles = result => result.items.map(item => item.title);

        // Stemming: "tomatoes" in the query and the instructions finds the recipe titled "Tomato".
        const tomato = await search({ q: 'tomatoes' });
        expect(titles(tomato)).toEqual(['Tomato soup']);
        expect(tomato.items[0]).toMatchObject({ id: soup.id, categoryName: category.name, cookingTimeMinutes: 30 });
        expect(tomato.items[0].score).toBeGreaterThan(0);

        // A match in the title ranks above a match in the instructions.
        expect(titles(await search({ q: 'garlic' }))).toEqual(['Garlic bread', 'Tomato soup']);
        expect(titles(await search({ q: 'garlic', maxMinutes: '20' }))).toEqual(['Garlic bread']);
        expect(titles(await search({ q: 'buttermilk' })), 'Ingredient names are searched').toEqual(['Pancakes']);
        expect(titles(await search({ q: 'crunchy' })), 'Descriptions are searched').toEqual(['Garlic bread']);
        expect(titles(await search({ q: 'БОРЩ' })), 'Cyrillic ignores case').toEqual(['Борщ']);
        expect(titles(await search({ q: 'бурЯк' }))).toEqual(['Борщ']);
        expect((await search({ q: 'garl*' })).total, 'Prefix query').toBe(2);
        expect(titles(await search({ q: 'title:garlic' })), 'Field query').toEqual(['Garlic bread']);
        expect(titles(await search({ q: 'garlic bread' })), 'Words are joined with AND').toEqual(['Garlic bread']);
        expect((await search({ q: 'garlic OR pancakes' })).total).toBe(3);
        expect((await search({ q: 'the' })).total, 'Stop words only').toBe(0);

        // Paging follows nextLink until the last page.
        const first = await search({ q: 'garlic', limit: '1' });
        expect(first).toMatchObject({ total: 2, skip: 0, limit: 1 });
        expect(first.nextLink).toMatch(/^\/api\/recipes\/search\?.*skip=1/);
        const second = await (await user.api.get(first.nextLink)).json();
        expect(titles(second)).toEqual(['Tomato soup']);
        expect(second.nextLink).toBeNull();

        // Bad requests.
        for (const query of ['q=title%3A(', 'q=', '', 'q=*soup', 'q=garlic&limit=101', 'q=garlic&maxMinutes=0']) {
            const response = await user.api.get('/api/recipes/search?' + query);
            expect(response.status(), query).toBe(400);
        }

        // The index follows changes made through the API, including deletes.
        expect((await user.api.put('/api/recipes/' + pancakes.id, { title: 'Fluffy pancakes', description: null,
            instructions: 'Whisk everything and fry.', cookingTimeMinutes: 20, servings: 2, categoryId: category.id })).status()).toBe(200);
        expect(titles(await search({ q: 'fluffy' }))).toEqual(['Fluffy pancakes']);
        expect((await user.api.delete('/api/recipes/' + bread.id)).status()).toBe(204);
        expect(titles(await search({ q: 'garlic' }))).toEqual(['Tomato soup']);
        expect((await user.api.put('/api/ingredients/' + buttermilk.id, { name: 'Kefir' })).status()).toBe(200);
        expect(titles(await search({ q: 'kefir' })), 'A renamed ingredient is found by its new name').toEqual(['Fluffy pancakes']);

        // Another kitchen and anonymous visitors (the public kitchen) never see these recipes.
        expect((await search({ q: 'tomato OR fluffy OR борщ' }, other)).total).toBe(0);
        const anonymous = await browser.newContext({ baseURL: app.baseURL });
        const publicResult = await (await anonymous.request.get('/api/recipes/search?q=fluffy')).json();
        expect(publicResult.total).toBe(0);
        await anonymous.close();
    } finally {
        await user.context.close();
        await other.context.close();
    }
});
