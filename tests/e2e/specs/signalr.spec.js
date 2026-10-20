import { test, expect } from '../fixtures.js';
import { handshake, kitchenUser } from '../helpers.js';

const recipe = (categoryId, title) => ({ title, instructions: 'Stir.', cookingTimeMinutes: 10, servings: 2, categoryId });

test('[C11-01] SignalR delivers kitchen changes over each of its transports and catches up', async ({ browser, app }) => {
    const user = await kitchenUser(browser, app, 'signalr');
    const other = await kitchenUser(browser, app, 'signalr-other');
    try {
        const [category] = await (await user.api.get('/api/categories')).json();
        const created = await (await user.api.post('/api/recipes', recipe(category.id, 'QA hub soup'))).json();
        const { page } = user;
        await page.goto('/Live');
        const feed = page.locator('#live-feed li');
        await page.getByLabel('SignalR', { exact: true }).check();
        const hub = url => /\/hubs\/kitchen\?id=/.test(url);
        // What the browser opens shows which transport SignalR really uses.
        const traffic = {
            'WebSockets': () => page.waitForEvent('websocket', socket => hub(socket.url())),
            'Server-Sent Events': () => page.waitForRequest(request => hub(request.url()) && request.headers().accept === 'text/event-stream'),
            'Long polling': () => page.waitForRequest(request => hub(request.url()) && request.method() === 'GET'
                && request.headers().accept !== 'text/event-stream')
        };
        for (const transport of ['Auto', 'WebSockets', 'Server-Sent Events', 'Long polling']) {
            const opened = traffic[transport]?.();
            await page.getByLabel('SignalR transport').selectOption({ label: transport });
            await opened;
            await expect(page.locator('#live-state')).toHaveText('Connected');
            // The catch-up right after connecting is the first message and holds nothing new.
            await expect(page.locator('#live-requests')).toHaveText('1');
            const title = `QA hub soup via ${transport}`;
            expect((await user.api.put('/api/recipes/' + created.id, recipe(category.id, title))).status()).toBe(200);
            await expect(feed.first()).toHaveText(new RegExp(`Recipe changed ${title}$`));
            await expect(page.locator('#live-events')).toHaveText('1');
        }

        const [otherCategory] = await (await other.api.get('/api/categories')).json();
        expect((await other.api.post('/api/recipes', recipe(otherCategory.id, 'QA other hub stew'))).status()).toBe(201);
        expect((await user.api.delete('/api/recipes/' + created.id)).status()).toBe(204);
        await expect(feed.first()).toHaveText(/Recipe deleted QA hub soup via Long polling$/);
        await expect(page.locator('#live-feed')).not.toContainText('QA other hub stew');

        // The hub's Since method returns the kitchen's own events after a given number.
        const caughtUp = await page.evaluate(async () => {
            const connection = new signalR.HubConnectionBuilder().withUrl('/hubs/kitchen').build();
            await connection.start();
            try { return await connection.invoke('Since', 0); } finally { await connection.stop(); }
        });
        expect(caughtUp.events.map(item => item.type)).toEqual(['created', 'updated', 'updated', 'updated', 'updated', 'deleted']);
        expect(caughtUp.last).toBe(6);
    } finally {
        await user.context.close();
        await other.context.close();
    }
});

test('[C11-02] the hub refuses negotiation and WebSockets from another origin', async ({ browser, app }) => {
    const user = await kitchenUser(browser, app, 'signalr-origin');
    try {
        const negotiate = origin => user.context.request.post('/hubs/kitchen/negotiate?negotiateVersion=1', { headers: { Origin: origin } });
        const allowed = await negotiate(app.baseURL);
        expect(allowed.status()).toBe(200);
        expect((await allowed.json()).availableTransports.map(item => item.transport)).toEqual(['WebSockets', 'ServerSentEvents', 'LongPolling']);
        expect((await negotiate('http://evil.example')).status()).toBe(403);
        // SignalR also accepts a WebSocket without negotiation, so the handshake is checked too.
        const cookie = (await user.context.cookies()).map(item => `${item.name}=${item.value}`).join('; ');
        expect(await handshake(app.baseURL + '/hubs/kitchen', cookie, app.baseURL)).toBe(101);
        expect(await handshake(app.baseURL + '/hubs/kitchen', cookie, 'http://evil.example')).toBe(403);
    } finally {
        await user.context.close();
    }
});
