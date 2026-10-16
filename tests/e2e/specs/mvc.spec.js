import { test, expect } from '../fixtures.js';
import { chooseCategory, fillRecipe, png, recipeId, row, save } from '../helpers.js';

for (const [controller, singular, id] of [['Categories', 'category', 'C1-01'], ['Ingredients', 'ingredient', 'C1-02']]) {
    test(`[${id}] ${singular} CRUD through browser forms`, async ({ page }) => {
        const original = `QA2 browser ${singular}`;
        const updated = original + ' edited';
        await page.goto('/' + controller);
        await page.getByRole('link', { name: 'Create ' + singular, exact: true }).click();
        await page.getByLabel('Name', { exact: true }).fill(original);
        await save(page, controller);
        await row(page, original).getByRole('link', { name: 'Details', exact: true }).click();
        await expect(page.locator('dd')).toHaveText(original);
        await page.getByRole('link', { name: 'Edit', exact: true }).click();
        await page.getByLabel('Name', { exact: true }).fill(updated);
        await save(page, controller);
        await expect(row(page, original)).toHaveCount(0);
        await row(page, updated).getByRole('link', { name: 'Delete', exact: true }).click();
        await expect(page.getByText(updated, { exact: true })).toBeVisible();
        await page.getByRole('button', { name: 'Delete', exact: true }).click();
        await expect(page).toHaveURL(new RegExp(`/${controller}$`));
        await expect(row(page, updated)).toHaveCount(0);
    });
}

test('[C1-03] category and ingredient names enforce required and length rules', async ({ page }) => {
    for (const controller of ['Categories', 'Ingredients']) {
        await page.goto(`/${controller}/Create`);
        await page.getByRole('button', { name: 'Save', exact: true }).click();
        await expect(page.locator('[data-valmsg-for="Name"]')).toContainText('required');
        await page.getByLabel('Name', { exact: true }).fill('N'.repeat(101));
        // Native maxlength/constraint validation can stop the normal submit before jQuery renders a message.
        // Submit an explicitly overlong value to verify the independent server rule as well.
        await page.getByLabel('Name', { exact: true }).evaluate(element => { element.value = 'N'.repeat(101); });
        const returned = page.waitForResponse(response => response.request().method() === 'POST' && response.url().endsWith(`/${controller}/Create`));
        await page.locator('main form').evaluate(form => form.submit());
        expect((await returned).status()).toBe(200);
        await expect(page.locator('[data-valmsg-for="Name"]')).toContainText('100');
        await expect(page).toHaveURL(new RegExp(`/${controller}/Create$`));
    }
});

test('[C1-04] recipe CRUD, landing content and persistent contact', async ({ page }) => {
    const title = 'QA2 browser recipe lifecycle';
    await page.goto('/Recipes');
    await page.getByRole('link', { name: 'Create recipe', exact: true }).click();
    await fillRecipe(page, title);
    await chooseCategory(page, 'Bre', 'Breakfast');
    await save(page);
    await row(page, title).getByRole('link', { name: 'Details', exact: true }).click();
    await expect(page.getByRole('heading', { name: title, exact: true })).toBeVisible();
    await expect(page.locator('.recipe-category')).toHaveText('Breakfast');
    await expect(page.locator('.recipe-facts')).toContainText('25 minutes');
    await expect(page.locator('#instructions')).toContainText('Mix the ingredients. Cook and serve.');
    await expect(page.getByText('No photo added', { exact: true })).toBeVisible();
    await expect(page.getByText('No ingredients listed.', { exact: true })).toBeVisible();
    const contact = page.getByRole('complementary', { name: 'Recipe contact' });
    await expect(contact.getByRole('link')).toHaveAttribute('href', 'mailto:vivtonichenko_dmytro@knu.ua');
    await page.locator('#instructions').scrollIntoViewIfNeeded();
    await expect(contact).toBeInViewport();
    await page.getByRole('link', { name: 'Edit recipe', exact: true }).first().click();
    await page.getByLabel('Title', { exact: true }).fill(title + ' edited');
    await chooseCategory(page, 'Des', 'Dessert');
    await save(page);
    await expect(row(page, title + ' edited')).toContainText('Dessert');
    await row(page, title + ' edited').getByRole('link', { name: 'Delete', exact: true }).click();
    await page.getByRole('button', { name: 'Delete', exact: true }).click();
    await expect(page).toHaveURL(/\/Recipes$/);
    await expect(row(page, title + ' edited')).toHaveCount(0);
});

test('[C1-05] required fields, positive numbers and forged category reject invalid submit', async ({ page }) => {
    await page.goto('/Recipes/Create');
    await page.getByRole('button', { name: 'Save', exact: true }).click();
    await expect(page.locator('[data-valmsg-for="Title"]')).toContainText('required');
    await expect(page.locator('[data-valmsg-for="Instructions"]')).toContainText('required');
    await expect(page.locator('#categoryValidation')).not.toHaveText('');
    await fillRecipe(page);
    await chooseCategory(page, 'Bre', 'Breakfast');
    await page.getByLabel('Cooking time (minutes)', { exact: true }).fill('0');
    await page.getByLabel('Servings', { exact: true }).fill('0');
    await page.getByRole('button', { name: 'Save', exact: true }).click();
    await expect(page).toHaveURL(/\/Recipes\/Create$/);
    expect(await page.locator('input[name="CookingTimeMinutes"]').evaluate(element => element.validity.valid)).toBe(false);
    await expect(page.locator('input[name="Servings"]')).toHaveValue('0');
    const returned = page.waitForResponse(response => response.request().method() === 'POST' && response.url().endsWith('/Recipes/Create'));
    await page.locator('main form').evaluate(form => form.submit());
    expect((await returned).status()).toBe(200);
    await expect(page.locator('[data-valmsg-for="CookingTimeMinutes"]')).not.toHaveText('');
    await expect(page.locator('[data-valmsg-for="Servings"]')).not.toHaveText('');
    await fillRecipe(page);
    await page.locator('#CategoryId').evaluate(select => {
        select.append(new Option('Forged category', '2147483647', true, true));
    });
    const forged = page.waitForResponse(response => response.request().method() === 'POST' && response.url().endsWith('/Recipes/Create'));
    await page.locator('main form').evaluate(form => form.submit());
    expect((await forged).status()).toBe(200);
    await expect(page.locator('#categoryValidation')).toContainText('existing category');
});

test('[C1-06] image upload, retained edit, replacement and deletion', async ({ page, request }) => {
    const title = 'QA2 browser photo lifecycle';
    await page.goto('/Recipes/Create');
    await fillRecipe(page, title);
    await chooseCategory(page, 'Bre', 'Breakfast');
    await page.getByLabel('Recipe image (optional)').setInputFiles({ name: 'first.png', mimeType: 'image/png', buffer: png });
    await save(page);
    const id = await recipeId(page, title);
    await page.goto('/Recipes/Details/' + id);
    const image = page.getByRole('img', { name: 'Image for ' + title, exact: true });
    const first = await image.getAttribute('src');
    await expect(image).toBeVisible();
    expect(await image.evaluate(element => element.complete && element.naturalWidth > 0)).toBe(true);
    expect((await request.get(first)).status()).toBe(200);
    await page.goto('/Recipes/Edit/' + id);
    await page.getByLabel('Description', { exact: true }).fill('Edited without replacing photo.');
    await save(page);
    await page.goto('/Recipes/Edit/' + id);
    await expect(page.getByRole('img')).toHaveAttribute('src', first);
    await page.getByLabel('Recipe image (optional)').setInputFiles({ name: 'replacement.png', mimeType: 'image/png', buffer: png });
    await save(page);
    await page.goto('/Recipes/Details/' + id);
    const replacement = await page.getByRole('img').getAttribute('src');
    expect(replacement).not.toBe(first);
    expect((await request.get(first)).status()).toBe(404);
    expect((await request.get(replacement)).status()).toBe(200);
    await page.goto('/Recipes/Delete/' + id);
    await page.getByRole('button', { name: 'Delete', exact: true }).click();
    await expect(page).toHaveURL(/\/Recipes$/);
    expect((await request.get(replacement)).status()).toBe(404);
});

test('[C1-07] unsupported, empty and oversized image uploads are rejected', async ({ page }) => {
    // The C15 script would stop these files in the browser; this test checks the server rules on their own.
    await page.route('**/js/image-upload.js*', route => route.abort());
    await page.goto('/Recipes/Create');
    await fillRecipe(page, 'QA2 rejected image');
    await chooseCategory(page, 'Bre', 'Breakfast');
    for (const file of [
        { name: 'unsafe.txt', mimeType: 'text/plain', buffer: Buffer.from('not an image'), error: 'Choose a JPG' },
        { name: 'empty.png', mimeType: 'image/png', buffer: Buffer.alloc(0), error: 'must not be empty' },
        { name: 'large.png', mimeType: 'image/png', buffer: Buffer.alloc(5 * 1024 * 1024 + 1), error: '5 MB' }
    ]) {
        await page.getByLabel('Recipe image (optional)').setInputFiles(file);
        const posted = page.waitForResponse(response => response.request().method() === 'POST' && response.url().endsWith('/Recipes/Create'));
        await page.getByRole('button', { name: 'Save', exact: true }).click();
        expect((await posted).status()).toBe(200);
        await expect(page.locator('[data-valmsg-for="imageFile"]')).toContainText(file.error);
        await expect(page.locator('#CategoryId')).toHaveValue('1');
    }
});

test('[C1-08] relationships prevent lookup deletion and survive recipe editing', async ({ page, data, request }) => {
    const category = await data.category('QA2 related category');
    const ingredient = await data.ingredient('QA2 related ingredient');
    const recipe = await data.recipe({ title: 'QA2 related recipe', categoryId: category.id });
    data.link(recipe.id, ingredient.id); // Test setup: MVC has no ingredient relationship editor.
    await page.goto('/Recipes/Details/' + recipe.id);
    await expect(page.locator('#ingredients')).toContainText('QA2 related ingredient');
    await expect(page.locator('.recipe-ingredient-amount')).toHaveText(/^2(?:[.,]0+)? pieces$/);
    for (const [controller, id] of [['Categories', category.id], ['Ingredients', ingredient.id]]) {
        await page.goto(`/${controller}/Delete/${id}`);
        await page.getByRole('button', { name: 'Delete', exact: true }).click();
        await expect(page.locator('.validation-summary-errors')).toContainText('cannot be deleted');
    }
    await page.goto('/Recipes/Edit/' + recipe.id);
    await page.getByLabel('Description', { exact: true }).fill('Relationship-preserving edit.');
    await save(page);
    await page.goto('/Recipes/Details/' + recipe.id);
    await expect(page.locator('.recipe-ingredient-amount')).toHaveText(/^2(?:[.,]0+)? pieces$/);
    await page.goto('/Recipes/Delete/' + recipe.id);
    await page.getByRole('button', { name: 'Delete', exact: true }).click();
    await expect(page).toHaveURL(/\/Recipes$/);
    expect((await request.get('/api/recipes/' + recipe.id)).status()).toBe(404);
    await page.goto('/Ingredients/Delete/' + ingredient.id);
    await page.getByRole('button', { name: 'Delete', exact: true }).click();
    await expect(page).toHaveURL(/\/Ingredients$/); // Cascade removed the link; ingredient is now unused.
});

test('[C1-09] missing details, edit and delete records return 404', async ({ page }) => {
    for (const controller of ['Recipes', 'Categories', 'Ingredients']) {
        for (const action of ['Details', 'Edit', 'Delete']) {
            const url = `/${controller}/${action}/2147483647`;
            const received = page.waitForResponse(response => new URL(response.url()).pathname === url);
            await page.goto(url).catch(error => {
                // Chrome rejects navigation to an empty error response; its real HTTP response is still asserted.
                if (!error.message.includes('ERR_HTTP_RESPONSE_CODE_FAILURE')) throw error;
            });
            const response = await received;
            expect(response.status()).toBe(404);
        }
    }
});

for (const [controller, id] of [['Categories', 'C1-10a'], ['Ingredients', 'C1-10b'], ['Recipes', 'C1-10c']]) {
    test(`[${id}] ${controller} submit without antiforgery token is rejected`, async ({ page }) => {
        await page.goto(`/${controller}/Create`);
        if (controller === 'Recipes') {
            await fillRecipe(page);
            await chooseCategory(page, 'Bre', 'Breakfast');
        } else await page.getByLabel('Name', { exact: true }).fill('QA2 blocked CSRF');
        await page.locator('main input[name="__RequestVerificationToken"]').evaluate(element => element.remove());
        const rejected = page.waitForResponse(response => response.request().method() === 'POST' && response.url().endsWith(`/${controller}/Create`));
        await page.getByRole('button', { name: 'Save', exact: true }).click();
        expect((await rejected).status()).toBe(400);
    });
}
