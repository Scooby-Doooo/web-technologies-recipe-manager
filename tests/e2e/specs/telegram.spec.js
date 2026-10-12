import { createServer } from 'node:http';
import { test, expect, startExtraServer } from '../fixtures.js';

// A stand-in for api.telegram.org: it hands queued updates to getUpdates and records sendMessage.
async function fakeTelegram() {
    const updates = [];
    const sent = [];
    let nextUpdateId = 1;
    const server = createServer((request, response) => {
        let body = '';
        request.on('data', chunk => { body += chunk; });
        request.on('end', () => {
            const method = request.url.split('/').at(-1);
            const reply = result => {
                response.writeHead(200, { 'Content-Type': 'application/json' });
                response.end(JSON.stringify({ ok: true, result }));
            };
            if (!request.url.startsWith('/bottest-token/')) {
                response.writeHead(401, { 'Content-Type': 'application/json' });
                response.end(JSON.stringify({ ok: false, description: 'Unauthorized' }));
            } else if (method === 'getMe') reply({ id: 1, is_bot: true, username: 'qa_test_bot' });
            else if (method === 'sendMessage') { sent.push(JSON.parse(body)); reply({ message_id: sent.length }); }
            else if (method === 'getUpdates') {
                // Long polling: answer at once when something is queued, otherwise after a short wait.
                const deliver = () => reply(updates.splice(0));
                if (updates.length) deliver(); else setTimeout(deliver, 300);
            } else reply(true);
        });
    });
    await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
    return {
        url: `http://127.0.0.1:${server.address().port}`,
        sent,
        send: (chatId, text) => updates.push({ update_id: nextUpdateId++,
            message: { message_id: nextUpdateId, chat: { id: chatId, type: 'private', first_name: `QA chat ${chatId}` }, text } }),
        close: () => new Promise(resolve => server.close(resolve))
    };
}

async function signedInClient(playwright, baseURL, email) {
    const client = await playwright.request.newContext({ baseURL });
    expect((await client.post('/Account/TestSignIn', { params: { email, kitchen: 'own' } })).status()).toBe(204);
    const { requestToken, headerName } = await (await client.get('/api/antiforgery')).json();
    const headers = { [headerName]: requestToken };
    const [category] = await (await client.get('/api/categories')).json();
    return {
        client,
        createRecipe: async (title, instructions = 'Boil.\nServe.') => {
            const response = await client.post('/api/recipes', { headers,
                data: { title, instructions, cookingTimeMinutes: 40, servings: 4, categoryId: category.id } });
            expect(response.status()).toBe(201);
            return response.json();
        },
        formToken: async () => (await (await client.get('/Account')).text())
            .match(/name="__RequestVerificationToken" type="hidden" value="([^"]+)"/)[1]
    };
}

test('[TG-01] the bot links a chat to a kitchen, answers commands and announces new recipes', async ({ app, playwright, browser }) => {
    const telegram = await fakeTelegram();
    const server = await startExtraServer(app.directory, 'telegram', 'Development', {
        Authentication__EnableTestSignIn: 'true',
        Telegram__BotToken: 'test-token',
        Telegram__ApiBaseUrl: telegram.url,
        Telegram__SiteUrl: 'https://recipes.example'
    });
    const alice = await signedInClient(playwright, server.baseURL, `qa2-tg-alice-${Date.now()}@example.test`);
    const bob = await signedInClient(playwright, server.baseURL, `qa2-tg-bob-${Date.now()}@example.test`);
    const reply = async (chatId, text) => {
        const before = telegram.sent.length;
        telegram.send(chatId, text);
        await expect.poll(() => telegram.sent.length, { message: `Bot answers "${text}"` }).toBeGreaterThan(before);
        const message = telegram.sent[before];
        expect(message.chat_id).toBe(chatId);
        expect(message.parse_mode).toBe('HTML');
        return message.text;
    };
    try {
        expect(await reply(111, '/recipes')).toContain('not linked to a kitchen');
        expect(await reply(111, '/start WRONGCODE1')).toContain('This link has expired');

        // The account page sends the user to the bot with a one-time code.
        const connect = await alice.client.post('/Account/ConnectTelegram',
            { form: { __RequestVerificationToken: await alice.formToken() }, maxRedirects: 0 });
        expect(connect.status()).toBe(302);
        const link = new URL(connect.headers().location);
        expect(link.origin + link.pathname).toBe('https://t.me/qa_test_bot');
        const code = link.searchParams.get('start');
        expect(code).toMatch(/^[A-Z2-9]{10}$/);

        expect(await reply(111, `/start ${code}`)).toContain("This chat is now linked to <b>Test user qa2-tg-alice-");
        expect(await reply(222, `/start ${code}`), 'The code works only once').toContain('This link has expired');

        const page = await (await browser.newContext({ baseURL: server.baseURL, storageState: await alice.client.storageState() })).newPage();
        await page.goto('/Account');
        await expect(page.locator('#telegram-chats')).toContainText('QA chat 111');
        await page.context().close();

        // A new recipe of Alice's kitchen is announced; one of Bob's kitchen is not.
        const soup = await alice.createRecipe('Soup <b>&</b> bread', 'Boil the water.\nAdd <vegetables>.');
        await expect.poll(() => telegram.sent.some(message => message.text.startsWith('New recipe:'))).toBe(true);
        const announcement = telegram.sent.find(message => message.text.startsWith('New recipe:'));
        expect(announcement.chat_id).toBe(111);
        expect(announcement.text).toContain('<b>Soup &lt;b&gt;&amp;&lt;/b&gt; bread</b>');
        expect(announcement.text).toContain(`https://recipes.example/Recipes/Details/${soup.id}`);
        await bob.createRecipe('QA2 Bob private recipe');

        const list = await reply(111, '/recipes');
        expect(list).toContain(`${soup.id}. Soup &lt;b&gt;&amp;&lt;/b&gt; bread — 40 min`);
        expect(list).not.toContain('Bob private');
        const full = await reply(111, '/recipe bread');
        expect(full).toContain('<b>Instructions</b>\nBoil the water.\nAdd &lt;vegetables&gt;.');
        expect(await reply(111, '/recipe Bob')).toBe('No recipe matches this name.');
        expect(await reply(111, '/random')).toContain('Soup');
        expect(await reply(111, '/help')).toContain('/recipes');

        expect(await reply(111, '/unlink')).toContain('no longer linked');
        expect(await reply(111, '/recipes')).toContain('not linked to a kitchen');
        expect(telegram.sent.filter(message => message.text.startsWith('New recipe:')), 'Bob has no linked chat').toHaveLength(1);
    } finally {
        await alice.client.dispose();
        await bob.client.dispose();
        await server.close();
        await telegram.close();
    }
});
