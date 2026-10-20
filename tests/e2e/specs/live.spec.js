import { test, expect } from '../fixtures.js';
import { handshake, kitchenUser } from '../helpers.js';

const recipe = (categoryId, title) => ({ title, instructions: 'Stir.', cookingTimeMinutes: 10, servings: 2, categoryId });

test('[C10-01] the live page shows kitchen changes through each of the four transports', async ({ browser, app }) => {
    const user = await kitchenUser(browser, app, 'live');
    const other = await kitchenUser(browser, app, 'live-other');
    try {
        const [category] = await (await user.api.get('/api/categories')).json();
        const created = await (await user.api.post('/api/recipes', recipe(category.id, 'QA live soup'))).json();
        const { page } = user;
        await page.goto('/Live');
        const feed = page.locator('#live-feed li');
        // Changes made before the page opened come as history and are not counted.
        await expect(feed.first()).toHaveText(/Recipe added QA live soup$/);
        await expect(page.locator('#live-events')).toHaveText('0');

        for (const [label, state] of [['WebSocket', 'Connected'], ['Polling every second', 'Polling'],
            ['Adaptive polling (1–16 s)', /^Next request in \d+ s$/], ['Long polling', 'Waiting for a change']]) {
            await page.getByLabel(label).check();
            await expect(page.locator('#live-state')).toHaveText(state);
            const title = `QA live soup via ${label}`;
            expect((await user.api.put('/api/recipes/' + created.id, recipe(category.id, title))).status()).toBe(200);
            await expect(feed.first()).toHaveText(new RegExp(`Recipe changed ${title.replace(/[()]/g, '\\$&')}$`));
            await expect(feed.first().getByRole('link')).toHaveAttribute('href', '/Recipes/Details/' + created.id);
            await expect(page.locator('#live-events')).toHaveText('1');
            expect(Number(await page.locator('#live-requests').textContent())).toBeGreaterThanOrEqual(1);
            await expect(page.locator('#live-average')).toHaveText(/^\d+ ms$/);
        }

        // Another kitchen's change never reaches this page.
        const [otherCategory] = await (await other.api.get('/api/categories')).json();
        expect((await other.api.post('/api/recipes', recipe(otherCategory.id, 'QA other kitchen stew'))).status()).toBe(201);
        expect((await user.api.delete('/api/recipes/' + created.id)).status()).toBe(204);
        await expect(feed.first()).toHaveText(/Recipe deleted QA live soup via Long polling$/);
        await expect(feed.first().getByRole('link')).toHaveCount(0);
        await expect(page.locator('#live-feed')).not.toContainText('QA other kitchen stew');
    } finally {
        await user.context.close();
        await other.context.close();
    }
});

test('[C10-02] feed endpoints answer at once, hold long polls and guard the WebSocket origin', async ({ browser, app }) => {
    const user = await kitchenUser(browser, app, 'live-protocol');
    try {
        const [category] = await (await user.api.get('/api/categories')).json();
        const created = await (await user.api.post('/api/recipes', recipe(category.id, 'QA protocol pie'))).json();
        const now = await user.api.get('/Live/Events?since=0');
        expect(now.headers()['cache-control']).toBe('no-store');
        const { last, events } = await now.json();
        expect(events.map(item => [item.type, item.recipeId, item.title])).toEqual([['created', created.id, 'QA protocol pie']]);
        expect(Math.abs(Date.now() - events[0].at)).toBeLessThan(60_000);
        // A number from before a server restart is ahead of the feed, so the whole feed comes back.
        expect((await (await user.api.get('/Live/Events?since=999')).json()).events).toHaveLength(1);

        // Nothing changes: the test server answers an empty long poll after 2 s.
        let started = Date.now();
        const quiet = await (await user.api.get(`/Live/Wait?since=${last}`)).json();
        expect(Date.now() - started).toBeGreaterThanOrEqual(1500);
        expect(quiet).toEqual({ last, events: [] });
        // A change during the wait answers it right away.
        started = Date.now();
        const waiting = user.api.get(`/Live/Wait?since=${last}`);
        await new Promise(resolve => setTimeout(resolve, 300));
        await user.api.put('/api/recipes/' + created.id, recipe(category.id, 'QA protocol pie, baked'));
        const woken = await (await waiting).json();
        expect(Date.now() - started).toBeLessThan(1500);
        expect(woken.events.map(item => item.title)).toEqual(['QA protocol pie, baked']);

        const cookie = (await user.context.cookies()).map(item => `${item.name}=${item.value}`).join('; ');
        expect(await handshake(app.baseURL + '/Live/Socket', cookie, app.baseURL)).toBe(101);
        expect(await handshake(app.baseURL + '/Live/Socket', cookie, 'http://evil.example')).toBe(403);
        expect((await user.api.get('/Live/Socket')).status()).toBe(400);
    } finally {
        await user.context.close();
    }
});
