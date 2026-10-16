import { readFile } from 'node:fs/promises';
import { test, expect } from '../fixtures.js';

const pixel = (page, x, y) => page.locator('#recipe-card-canvas').evaluate((canvas, [x, y]) =>
    Array.from(canvas.getContext('2d').getImageData(x, y, 1, 1).data.slice(0, 3)), [x, y]);

async function openCard(page, id) {
    await page.goto('/Recipes/Details/' + id);
    await expect(page.getByRole('button', { name: 'Download PNG' })).toBeEnabled();
}

test('[F6-01] recipe card draws the photo and follows the format, colour and ingredient options', async ({ page, request, data }) => {
    const ingredient = await data.ingredient('QA2 card flour');
    const recipe = await data.recipe({ title: 'QA2 recipe card' });
    data.link(recipe.id, ingredient.id);
    // A plain red photo makes it easy to tell where the canvas drew it.
    await page.goto('/');
    const red = Buffer.from(await page.evaluate(() => {
        const canvas = document.createElement('canvas');
        canvas.width = 400;
        canvas.height = 300;
        const context = canvas.getContext('2d');
        context.fillStyle = '#ff0000';
        context.fillRect(0, 0, 400, 300);
        return canvas.toDataURL('image/png').split(',')[1];
    }), 'base64');
    const { requestToken, headerName } = await (await request.get('/api/antiforgery')).json();
    const upload = await request.post(`/api/recipes/${recipe.id}/image`, {
        headers: { [headerName]: requestToken },
        multipart: { imageFile: { name: 'red.png', mimeType: 'image/png', buffer: red } }
    });
    expect(upload.status()).toBe(200);

    await openCard(page, recipe.id);
    const canvas = page.locator('#recipe-card-canvas');
    await expect(canvas).toHaveJSProperty('height', 1080);
    expect(await pixel(page, 540, 222)).toEqual([255, 0, 0]);
    expect(await pixel(page, 2, 1077)).toEqual([23, 103, 79]); // green theme background
    // With ingredients the photo is 300 px high; without them it grows to 440 px.
    expect(await pixel(page, 540, 472)).not.toEqual([255, 0, 0]);
    await page.getByLabel('Show ingredients').uncheck();
    expect(await pixel(page, 540, 472)).toEqual([255, 0, 0]);

    await page.getByLabel('Colours').selectOption('dark');
    expect(await pixel(page, 2, 1077)).toEqual([22, 33, 31]);
    await page.getByLabel('Format').selectOption('story');
    await expect(canvas).toHaveJSProperty('height', 1920);
    expect(await pixel(page, 540, 800)).toEqual([255, 0, 0]);

    const downloaded = page.waitForEvent('download');
    await page.getByRole('button', { name: 'Download PNG' }).click();
    const file = await downloaded;
    expect(file.suggestedFilename()).toBe(`recipe-${recipe.id}-story.png`);
    const png = await readFile(await file.path());
    expect(png.subarray(1, 4).toString()).toBe('PNG');
    expect([png.readUInt32BE(16), png.readUInt32BE(20)]).toEqual([1080, 1920]);
});

test('[F6-02] recipe card without a photo draws a plate and survives long text', async ({ page, data }) => {
    const recipe = await data.recipe({ title: 'QA2 card ' + 'Supercalifragilisticexpialidocious'.repeat(4) + ' with a very long title' });
    for (let i = 1; i <= 9; i++) data.link(recipe.id, (await data.ingredient(`QA2 card ingredient ${i} with a long name`)).id);
    await openCard(page, recipe.id);
    expect(await pixel(page, 540, 222)).toEqual([31, 122, 95]); // plate background in the green theme
    await page.getByLabel('Format').selectOption('story');
    await expect(page.locator('#recipe-card-canvas')).toHaveJSProperty('height', 1920);
    await expect(page.getByRole('img', { name: 'Recipe card for ' + recipe.title })).toBeVisible();
});
