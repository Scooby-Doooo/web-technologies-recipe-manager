import { readFile } from 'node:fs/promises';
import { test, expect } from '../fixtures.js';

const blocks = page => page.locator('#notes-document .notes-block');
const shape = page => blocks(page).evaluateAll(rows => rows.map(row =>
    [row.className.match(/notes-(paragraph|heading\d|bullet|numbered|todo|quote)/)[1], row.querySelector('.notes-text').textContent]));

test('[C12-01] Markdown shortcuts build blocks that survive a reload and a new browser', async ({ page, data, browser, baseURL }) => {
    const recipe = await data.recipe({ title: 'QA notes pie' });
    await page.goto('/Recipes/Details/' + recipe.id);
    await page.getByRole('link', { name: 'Notes', exact: true }).click();
    await expect(page).toHaveURL('/Recipes/Notes/' + recipe.id);
    await expect(blocks(page)).toHaveCount(2);

    await blocks(page).nth(1).locator('.notes-text').click();
    const keyboard = page.keyboard;
    await keyboard.type('# Shopping');
    await keyboard.press('Enter');
    await keyboard.type('- Milk');
    await keyboard.press('Enter');
    await keyboard.type('Eggs');
    await keyboard.press('Enter');
    await keyboard.press('Enter'); // an empty list item ends the list
    await keyboard.type('1. Mix');
    await keyboard.press('Enter');
    await keyboard.type('Bake');
    await keyboard.press('Enter');
    await keyboard.press('Enter');
    await keyboard.type('[] Buy flour');
    await keyboard.press('Enter');
    await keyboard.press('Enter');
    await keyboard.type("> Grandma's tip");
    await keyboard.press('Enter');
    await keyboard.type('Use **cold** butter, see [video](https://example.com/v) at `180C` <img src=x onerror=alert(1)> [bad](javascript:alert(1))');
    await blocks(page).filter({ hasText: 'Buy flour' }).getByRole('checkbox', { name: 'Done' }).check();
    await page.locator('h1').click(); // leaving the block shows its Markdown rendered

    const expected = [['heading1', 'Notes for QA notes pie'], ['heading1', 'Shopping'], ['bullet', 'Milk'], ['bullet', 'Eggs'],
        ['numbered', 'Mix'], ['numbered', 'Bake'], ['todo', 'Buy flour'], ['quote', "Grandma's tip"],
        ['paragraph', 'Use cold butter, see video at 180C <img src=x onerror=alert(1)> [bad](javascript:alert(1))']];
    expect(await shape(page)).toEqual(expected);
    await expect(blocks(page).locator('.notes-marker')).toHaveText(['•', '•', '1.', '2.']);
    const last = blocks(page).last();
    await expect(last.locator('strong')).toHaveText('cold');
    await expect(last.getByRole('link', { name: 'video' })).toHaveAttribute('href', 'https://example.com/v');
    await expect(last.locator('code')).toHaveText('180C');
    await expect(last.locator('img')).toHaveCount(0);
    await expect(last.getByRole('link')).toHaveCount(1);
    await expect(page.locator('#notes-status')).toHaveText(/^Saved in this browser at /);

    // The focused block shows its raw Markdown again.
    await last.locator('.notes-text').click();
    await expect(last.locator('.notes-text')).toContainText('**cold**');
    await page.locator('h1').click();

    const downloaded = page.waitForEvent('download');
    await page.getByRole('button', { name: 'Download .md' }).click();
    const file = await downloaded;
    expect(file.suggestedFilename()).toBe(`recipe-${recipe.id}-notes.md`);
    expect(await readFile(await file.path(), 'utf8')).toBe([
        '# Notes for QA notes pie', '', '# Shopping', '', '- Milk', '- Eggs', '', '1. Mix', '2. Bake', '', '- [x] Buy flour', '',
        "> Grandma's tip", '', 'Use **cold** butter, see [video](https://example.com/v) at `180C` <img src=x onerror=alert(1)> [bad](javascript:alert(1))', ''
    ].join('\n'));

    await page.reload();
    await expect(page.locator('#notes-status')).toHaveText('Restored from this browser');
    expect(await shape(page)).toEqual(expected);
    await expect(blocks(page).nth(6)).toHaveClass(/notes-done/);
    // A new browser with the same saved site data, as after closing and opening the browser.
    const reopened = await browser.newContext({ baseURL, storageState: await page.context().storageState() });
    try {
        const again = await reopened.newPage();
        await again.goto('/Recipes/Notes/' + recipe.id);
        expect(await shape(again)).toEqual(expected);
    } finally {
        await reopened.close();
    }
});

test('[C12-02] Enter, Backspace, Delete, line breaks, paste and clearing edit the block list', async ({ page, data }) => {
    const recipe = await data.recipe({ title: 'QA notes stew' });
    await page.goto('/Recipes/Notes/' + recipe.id);
    const text = index => blocks(page).nth(index).locator('.notes-text');
    await text(1).click();
    await page.keyboard.type('- Carrot');
    await page.keyboard.press('Enter');
    await page.keyboard.type('Onion');
    // Backspace at the start of a list item turns it into text, then joins it to the block above.
    await page.keyboard.press('Home');
    await page.keyboard.press('Backspace');
    expect((await shape(page))[2]).toEqual(['paragraph', 'Onion']);
    await page.keyboard.press('Backspace');
    expect((await shape(page)).slice(1)).toEqual([['bullet', 'CarrotOnion']]);
    // The caret stays at the joint; Enter there splits the block again, Delete at the end joins them back.
    await page.keyboard.press('Enter');
    expect((await shape(page)).slice(1)).toEqual([['bullet', 'Carrot'], ['bullet', 'Onion']]);
    await page.keyboard.press('ArrowUp');
    await page.keyboard.press('Delete');
    expect((await shape(page)).slice(1)).toEqual([['bullet', 'CarrotOnion']]);
    await page.keyboard.press('End');
    await page.keyboard.press('Enter');
    await page.keyboard.press('Enter');
    await page.keyboard.type('first line');
    await page.keyboard.press('Shift+Enter');
    await page.keyboard.type('second line');
    expect((await shape(page)).at(-1)).toEqual(['paragraph', 'first line\nsecond line']);

    // The toolbar changes the type of the current block.
    await page.getByLabel('Block').selectOption('Heading 2');
    expect((await shape(page)).at(-1)).toEqual(['heading2', 'first line\nsecond line']);

    // Pasted Markdown lines become blocks after the caret.
    await page.keyboard.press('Enter');
    await text(-1).evaluate(element => {
        const transfer = new DataTransfer();
        transfer.setData('text/plain', '## Sauce\r\n- [ ] Tomatoes\n\n1. Simmer\n> Taste it');
        element.dispatchEvent(new ClipboardEvent('paste', { clipboardData: transfer, bubbles: true, cancelable: true }));
    });
    expect((await shape(page)).slice(-4)).toEqual([['heading2', 'Sauce'], ['todo', 'Tomatoes'], ['numbered', 'Simmer'], ['quote', 'Taste it']]);

    // Broken saved data does not break the page; it starts a new document.
    await expect(page.locator('#notes-status')).toHaveText(/^Saved in this browser at /);
    await page.evaluate(id => localStorage.setItem(`recipe-manager-notes-${id}`, '{"version":1,"blocks":[{"type":"script"}]}'), recipe.id);
    await page.reload();
    expect(await shape(page)).toEqual([['heading1', 'Notes for QA notes stew'], ['paragraph', '']]);
    await text(1).click();
    await page.keyboard.type('Temporary');
    page.once('dialog', dialog => dialog.accept());
    await page.getByRole('button', { name: 'Clear notes' }).click();
    expect(await shape(page)).toEqual([['paragraph', '']]);
    await page.reload();
    expect(await shape(page)).toEqual([['paragraph', '']]);
});

test('[F3-01] Blocks are sorted by dragging or with the keyboard, slide to their place and keep the order', async ({ page, data }) => {
    const recipe = await data.recipe({ title: 'QA notes soup' });
    await page.goto('/Recipes/Notes/' + recipe.id);
    await page.evaluate(id => localStorage.setItem(`recipe-manager-notes-${id}`, JSON.stringify({ version: 1, blocks: [
        { id: 'a', type: 'numbered', text: 'Chop' }, { id: 'b', type: 'numbered', text: 'Fry' },
        { id: 'c', type: 'numbered', text: 'Boil' }, { id: 'd', type: 'paragraph', text: 'Serve hot' }] })), recipe.id);
    await page.reload();
    const handle = text => blocks(page).filter({ hasText: text }).getByRole('button', { name: 'Drag to reorder' });

    // Dropping on the upper half of a block puts the dragged one above it; list numbers follow the new order.
    await handle('Boil').dragTo(blocks(page).first(), { targetPosition: { x: 40, y: 2 } });
    expect(await shape(page)).toEqual([['numbered', 'Boil'], ['numbered', 'Chop'], ['numbered', 'Fry'], ['paragraph', 'Serve hot']]);
    await expect(blocks(page).locator('.notes-marker')).toHaveText(['1.', '2.', '3.']);

    // A drag that ends without a drop (Esc, or released outside) puts the blocks back.
    const during = await page.locator('#notes-document').evaluate(view => {
        const [first, , third] = view.querySelectorAll('.notes-block');
        const fire = (target, type, y = 0) => target.dispatchEvent(new DragEvent(type,
            { bubbles: true, cancelable: true, clientY: y, dataTransfer: new DataTransfer() }));
        fire(first.querySelector('.notes-handle'), 'dragstart');
        fire(third, 'dragover', third.getBoundingClientRect().bottom - 1);
        const order = [...view.querySelectorAll('.notes-text')].map(text => text.textContent);
        fire(first.querySelector('.notes-handle'), 'dragend');
        return order;
    });
    expect(during).toEqual(['Chop', 'Fry', 'Boil', 'Serve hot']);
    expect((await shape(page)).map(([, text]) => text)).toEqual(['Boil', 'Chop', 'Fry', 'Serve hot']);

    // From the keyboard the focused handle moves its block one place, with the slide animation.
    await handle('Serve hot').focus();
    await page.keyboard.press('ArrowUp');
    expect(await blocks(page).evaluateAll(rows => rows.some(row => row.getAnimations().length))).toBe(true);
    await expect(handle('Serve hot')).toBeFocused();
    await expect(page.locator('#notes-status')).toHaveText('Moved to position 3 of 4');
    await page.keyboard.press('ArrowUp');
    await page.keyboard.press('ArrowDown');
    expect((await shape(page)).map(([, text]) => text)).toEqual(['Boil', 'Chop', 'Serve hot', 'Fry']);

    await page.reload();
    expect((await shape(page)).map(([, text]) => text)).toEqual(['Boil', 'Chop', 'Serve hot', 'Fry']);
    await expect(blocks(page).locator('.notes-marker')).toHaveText(['1.', '2.', '1.']);
});
