import { test, expect } from '../fixtures.js';
import { kitchenUser } from '../helpers.js';

const blocks = page => page.locator('#notes-document .notes-block');
const text = (page, index) => blocks(page).nth(index).locator('.notes-text');
const status = page => page.locator('#notes-status');
const shape = page => blocks(page).evaluateAll(rows => rows.map(row =>
    [row.className.match(/notes-(paragraph|heading\d|bullet|numbered|todo|quote)/)[1], row.querySelector('.notes-text').textContent]));

// The same account signed in on another device: the cookie is copied, the browser storage is not.
async function device(browser, app, user) {
    const { cookies } = await user.context.storageState();
    const context = await browser.newContext({ baseURL: app.baseURL, storageState: { cookies, origins: [] } });
    return { context, page: await context.newPage() };
}

async function openNotes(page, recipeId) {
    await page.goto('/Recipes/Notes/' + recipeId);
    await expect(status(page)).toHaveText('Up to date');
}

async function withDevices(browser, app, run) {
    const user = await kitchenUser(browser, app, 'notes-sync');
    const other = await device(browser, app, user);
    try {
        const [category] = await (await user.api.get('/api/categories')).json();
        const recipe = await (await user.api.post('/api/recipes', { title: 'QA sync pie', instructions: 'Bake.',
            cookingTimeMinutes: 30, servings: 4, categoryId: category.id })).json();
        await run(user, other, recipe);
    } finally {
        await other.context.close();
        await user.context.close();
    }
}

test('[C13-01] Edits on one device appear on the other, and edits of different blocks at once both survive', async ({ browser, app }) => {
    await withDevices(browser, app, async (user, { page: second }, recipe) => {
        const first = user.page;
        await openNotes(first, recipe.id);
        await openNotes(second, recipe.id);
        expect(await shape(second)).toEqual([['heading1', 'Notes for QA sync pie'], ['paragraph', '']]);

        await text(first, 1).click();
        await first.keyboard.type('Buy milk');
        await expect(text(second, 1)).toHaveText('Buy milk');

        // Both devices type at the same time, each into its own block; nothing is lost on either.
        await text(first, 0).click();
        await first.keyboard.press('End');
        await text(second, 1).click();
        await second.keyboard.press('End');
        await Promise.all([first.keyboard.type(' for Sunday', { delay: 30 }), second.keyboard.type(' and eggs', { delay: 30 })]);
        const typed = [['heading1', 'Notes for QA sync pie for Sunday'], ['paragraph', 'Buy milk and eggs']];
        await expect.poll(() => shape(first)).toEqual(typed);
        await expect.poll(() => shape(second)).toEqual(typed);
        // The caret of the device that was typing stayed in its block.
        await second.keyboard.type('!');
        await expect(text(first, 1)).toHaveText('Buy milk and eggs!');

        // New blocks, types and order travel too.
        await second.keyboard.press('Enter');
        await second.keyboard.type('- Flour');
        await expect.poll(() => shape(first)).toEqual([...typed.slice(0, 1), ['paragraph', 'Buy milk and eggs!'], ['bullet', 'Flour']]);
        await blocks(first).filter({ hasText: 'Flour' }).getByRole('button', { name: 'Drag to reorder' }).focus();
        await first.keyboard.press('ArrowUp');
        await expect.poll(() => shape(second)).toEqual([...typed.slice(0, 1), ['bullet', 'Flour'], ['paragraph', 'Buy milk and eggs!']]);
        await expect(status(first)).toHaveText(/^Saved at /);

        // The document is kept on the server: a third device with empty storage gets it.
        const third = await device(browser, app, user);
        try {
            await openNotes(third.page, recipe.id);
            expect(await shape(third.page)).toEqual(await shape(first));
        } finally {
            await third.context.close();
        }
    });
});

test('[C13-02] Edits kept in the browser while offline are merged into the newer server document', async ({ browser, app }) => {
    await withDevices(browser, app, async (user, { page: second }, recipe) => {
        const first = user.page;
        await openNotes(first, recipe.id);
        await openNotes(second, recipe.id);
        // An edit the first device made but could not send: it is only in its copy in localStorage.
        await first.evaluate(id => {
            const key = `recipe-manager-notes-${id}`;
            const saved = JSON.parse(localStorage.getItem(key));
            saved.blocks[1].text = 'Typed offline';
            localStorage.setItem(key, JSON.stringify(saved));
        }, recipe.id);
        await first.close();

        // Meanwhile the other device changes the title.
        await text(second, 0).click();
        await second.keyboard.press('End');
        await second.keyboard.type(' (v2)');
        await expect(status(second)).toHaveText(/^Saved at /);

        const back = await user.context.newPage();
        await back.goto('/Recipes/Notes/' + recipe.id);
        const merged = [['heading1', 'Notes for QA sync pie (v2)'], ['paragraph', 'Typed offline']];
        await expect.poll(() => shape(back)).toEqual(merged);
        await expect(status(back)).toHaveText(/^Saved at /);
        await expect.poll(() => shape(second)).toEqual(merged);
    });
});

test('[C13-03] The hub checks the kitchen and every change before saving it', async ({ browser, app }) => {
    await withDevices(browser, app, async (user, _, recipe) => {
        const stranger = await kitchenUser(browser, app, 'notes-stranger');
        try {
            // The Live page loads the SignalR client, so the hub can be called directly from it.
            const call = (page, method, ...args) => page.evaluate(async ([method, args]) => {
                const connection = new window.signalR.HubConnectionBuilder().withUrl('/hubs/kitchen').build();
                await connection.start();
                try {
                    return { ok: await connection.invoke(method, ...args) };
                } catch (error) {
                    return { error: error.message.replace(/^.*HubException: /, '') };
                } finally {
                    await connection.stop();
                }
            }, [method, args]);
            await stranger.page.goto('/Live');
            const change = { upserts: [{ id: 'title', type: 'heading1', text: 'Taken over', checked: false }], deletes: [], order: null };
            expect(await call(stranger.page, 'OpenNotes', recipe.id)).toEqual({ error: 'The recipe was not found.' });
            expect(await call(stranger.page, 'SaveNotes', recipe.id, change)).toEqual({ error: 'The recipe was not found.' });

            await user.page.goto('/Live');
            const bad = block => ({ upserts: [{ id: 'x', type: 'paragraph', text: '', checked: false, ...block }], deletes: [], order: null });
            expect(await call(user.page, 'SaveNotes', recipe.id, bad({ type: 'script' }))).toEqual({ error: 'The change has an invalid block.' });
            expect(await call(user.page, 'SaveNotes', recipe.id, bad({ text: 'x'.repeat(5001) }))).toEqual({ error: 'A block can hold up to 5000 characters.' });
            expect(await call(user.page, 'SaveNotes', recipe.id, { upserts: [bad({}).upserts[0], bad({}).upserts[0]], deletes: [], order: null }))
                .toEqual({ error: 'The change has the same block twice.' });
            // Nothing of the above was saved.
            expect(await call(user.page, 'OpenNotes', recipe.id)).toEqual({ ok: { version: 0, blocks: [
                { id: 'title', type: 'heading1', text: 'Notes for QA sync pie', checked: false },
                { id: 'start', type: 'paragraph', text: '', checked: false }] } });
        } finally {
            await stranger.context.close();
        }
    });
});
