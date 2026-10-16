import { test, expect } from '../fixtures.js';
import { chooseCategory, fillRecipe, png, recipeId, save } from '../helpers.js';

// Random pixels do not compress well, so a 2400 × 1800 PNG is larger than the 5 MB server limit.
async function noisyPng(page) {
    const base64 = await page.evaluate(async () => {
        const canvas = document.createElement('canvas');
        canvas.width = 2400;
        canvas.height = 1800;
        const context = canvas.getContext('2d');
        const pixels = context.createImageData(canvas.width, canvas.height);
        // getRandomValues fills at most 64 KB per call.
        for (let i = 0; i < pixels.data.length; i += 65536) crypto.getRandomValues(pixels.data.subarray(i, i + 65536));
        for (let i = 3; i < pixels.data.length; i += 4) pixels.data[i] = 255;
        context.putImageData(pixels, 0, 0);
        const blob = await new Promise(resolve => canvas.toBlob(resolve, 'image/png'));
        const bytes = new Uint8Array(await blob.arrayBuffer());
        let text = '';
        for (let i = 0; i < bytes.length; i += 32768) text += String.fromCharCode(...bytes.subarray(i, i + 32768));
        return btoa(text);
    });
    return Buffer.from(base64, 'base64');
}

test('[C15-01] a large photo is previewed, resized and compressed before upload', async ({ page, request }) => {
    const title = 'QA2 compressed photo';
    await page.goto('/Recipes/Create');
    const original = await noisyPng(page);
    expect(original.length).toBeGreaterThan(5 * 1024 * 1024);
    await fillRecipe(page, title);
    await chooseCategory(page, 'Bre', 'Breakfast');
    await page.getByLabel('Recipe image (optional)').setInputFiles({ name: 'big photo.png', mimeType: 'image/png', buffer: original });
    await expect(page.getByRole('img', { name: 'Preview of the chosen image' })).toBeVisible();
    await expect(page.locator('#image-info')).toContainText('big photo.webp: 1600 × 1200 px');
    await expect(page.locator('#image-info')).toContainText('compressed from');
    const chosen = await page.locator('#imageFile').evaluate(input => ({ name: input.files[0].name, type: input.files[0].type, size: input.files[0].size }));
    expect(chosen).toMatchObject({ name: 'big photo.webp', type: 'image/webp' });
    expect(chosen.size).toBeLessThan(5 * 1024 * 1024);
    await save(page);
    const id = await recipeId(page, title);
    await page.goto('/Recipes/Details/' + id);
    const image = page.getByRole('img', { name: 'Image for ' + title, exact: true });
    await expect(image).toHaveAttribute('src', /\.webp$/);
    expect(await image.evaluate(element => element.complete ? element.naturalWidth : 0)).toBe(1600);
    expect((await request.get(await image.getAttribute('src'))).headers()['content-length']).toBe(String(chosen.size));
    await page.goto('/Recipes/Delete/' + id);
    await page.getByRole('button', { name: 'Delete', exact: true }).click();
    await expect(page).toHaveURL(/\/Recipes$/);
});

test('[C15-02] small photos are kept as they are and unsupported files are stopped in the browser', async ({ page }) => {
    await page.goto('/Recipes/Create');
    const field = page.getByLabel('Recipe image (optional)');
    await field.setInputFiles({ name: 'small.png', mimeType: 'image/png', buffer: png });
    await expect(page.locator('#image-info')).toHaveText(`small.png: 1 × 1 px, ${png.length} B`);
    expect(await page.locator('#imageFile').evaluate(input => input.files[0].size)).toBe(png.length);
    for (const [file, message] of [
        [{ name: 'notes.txt', mimeType: 'text/plain', buffer: Buffer.from('not an image') }, 'Choose a JPG'],
        [{ name: 'broken.png', mimeType: 'image/png', buffer: Buffer.from('not really a png') }, 'cannot be read']
    ]) {
        await field.setInputFiles(file);
        await expect(page.getByRole('alert')).toContainText(message);
        await expect(page.locator('#image-preview')).toBeHidden();
        expect(await page.locator('#imageFile').evaluate(input => input.files.length)).toBe(0);
    }
});
