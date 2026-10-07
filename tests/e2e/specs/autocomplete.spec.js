import { test, expect, signIn } from '../fixtures.js';
import { chooseCategory, fillRecipe, save, search } from '../helpers.js';

const lookup = response => response.url().includes('/Recipes/CategoryOptions?');

test('[F2-01] minimum length, 300 ms delay and maximum input length', async ({ page }) => {
    await page.clock.install();
    const requests = [];
    page.on('request', request => { if (request.url().includes('/Recipes/CategoryOptions?')) requests.push(request.url()); });
    await page.goto('/Recipes/Create');
    const input = await search(page, 'Sa');
    await page.clock.runFor(400);
    expect(requests).toHaveLength(0);
    await expect(page.locator('.select2-results')).toContainText('1 or more characters');
    await input.pressSequentially('lad');
    await page.clock.runFor(299);
    expect(requests).toHaveLength(0);
    const found = page.waitForResponse(lookup);
    await page.clock.runFor(1);
    expect((await found).status()).toBe(200);
    expect(requests).toHaveLength(1);
    expect(new URL(requests[0]).searchParams.get('term')).toBe('Salad');
    await expect(page.getByRole('option', { name: 'Salad', exact: true })).toBeVisible();
    await input.fill('x'.repeat(101));
    await page.clock.runFor(400);
    expect(requests).toHaveLength(1);
    await expect(page.locator('.select2-results')).toContainText('delete 1 character');
});

test('[F2-02] scrolling fetches bounded result pages from the real backend', async ({ page, data }) => {
    for (let i = 0; i < 45; i++) await data.category('QA2 orchard ' + String(i).padStart(2, '0'));
    await page.goto('/Recipes/Create');
    const first = page.waitForResponse(lookup);
    await search(page, 'QA2 orchard');
    const payload = await (await first).json();
    expect(payload.results).toHaveLength(20);
    expect(payload.pagination.more).toBe(true);
    const options = page.getByRole('option').filter({ hasText: /^QA2 orchard/ });
    await expect(options).toHaveCount(20);
    for (const [number, count] of [[2, 40], [3, 45]]) {
        const next = page.waitForResponse(response => lookup(response) && new URL(response.url()).searchParams.get('page') === String(number));
        await page.locator('.select2-results__options').evaluate(element => { element.scrollTop = element.scrollHeight; });
        const body = await (await next).json();
        expect(body.results.length).toBeLessThanOrEqual(20);
        expect(body.pagination.more).toBe(number === 2);
        await expect(options).toHaveCount(count);
    }
});

test('[F2-03] duplicate IDs, literal wildcards, Unicode and empty results', async ({ page, data }) => {
    const first = await data.category('QA2 duplicate');
    const second = await data.category('QA2 duplicate');
    const literal = await data.category('QA2 literal %_% \\slash');
    const unicode = await data.category('Категорія Київ QA2');
    await page.goto('/Recipes/Create');
    let response = page.waitForResponse(lookup);
    const input = await search(page, 'QA2 duplicate');
    expect((await (await response).json()).results.map(item => item.id)).toEqual([first.id, second.id]);
    await page.getByRole('option', { name: 'QA2 duplicate', exact: true }).nth(1).click();
    await expect(page.locator('#CategoryId')).toHaveValue(String(second.id));
    await page.getByRole('combobox', { name: /^Category/ }).click();
    for (const [term, expected] of [['%_%', literal.id], ['\\slash', literal.id], ['Категорія', unicode.id], ['QA2 nonexistent', null]]) {
        response = page.waitForResponse(lookup);
        await input.fill('');
        await input.pressSequentially(term);
        const body = await (await response).json();
        expect(body.results.map(item => item.id)).toEqual(expected === null ? [] : [expected]);
        if (expected === null) await expect(page.locator('.select2-results')).toContainText('No results found');
    }
});

test('[F2-04] edit preselection and keyboard category change persist', async ({ page, data }) => {
    const recipe = await data.recipe({ title: 'QA2 keyboard edit' });
    const requests = [];
    page.on('request', request => { if (request.url().includes('/Recipes/CategoryOptions?')) requests.push(request.url()); });
    await page.goto('/Recipes/Edit/' + recipe.id);
    await expect(page.getByRole('combobox', { name: /^Category/ })).toContainText('Breakfast');
    await expect(page.locator('#CategoryId option')).toHaveCount(2);
    expect(requests).toHaveLength(0);
    const input = await search(page, 'Main');
    await expect(page.getByRole('option', { name: 'Main Course', exact: true })).toBeVisible();
    await input.press('Enter');
    await expect(page.locator('#CategoryId')).toHaveValue('2');
    await save(page);
    await page.goto('/Recipes/Details/' + recipe.id);
    await expect(page.locator('.recipe-category')).toHaveText('Main Course');
});

test('[F2-05] visible validation and server validation retain the selected category', async ({ page }) => {
    await page.goto('/Recipes/Create');
    await fillRecipe(page);
    await chooseCategory(page, 'Bre', 'Breakfast');
    await page.locator('.select2-selection__clear').click();
    await page.getByRole('button', { name: 'Save', exact: true }).click();
    await expect(page.getByRole('combobox', { name: /^Category/ })).toHaveAttribute('aria-invalid', 'true');
    await chooseCategory(page, 'Des', 'Dessert');
    await expect(page.getByRole('combobox', { name: /^Category/ })).toHaveAttribute('aria-invalid', 'false');
    await page.getByLabel('Title', { exact: true }).fill('');
    await page.getByRole('button', { name: 'Save', exact: true }).click();
    await expect(page.locator('[data-valmsg-for="Title"]')).toContainText('required');
    await expect(page.locator('#CategoryId')).toHaveValue('3');
    const returned = page.waitForResponse(response => response.request().method() === 'POST' && response.url().endsWith('/Recipes/Create'));
    await page.locator('main form').evaluate(form => form.submit()); // Exercise server validation independently of the client gate.
    expect((await returned).status()).toBe(200);
    await expect(page.locator('#CategoryId')).toHaveValue('3');
    await expect(page.getByRole('combobox', { name: /^Category/ })).toContainText('Dessert');
});

test('[F2-06] HTML-like category labels are displayed as inert text', async ({ page, data }) => {
    const name = 'QA2 <img src=x onerror=window.qa2Injected=true>';
    const category = await data.category(name);
    await page.goto('/Recipes/Create');
    await search(page, 'QA2 <img');
    const option = page.getByRole('option', { name, exact: true });
    await expect(option).toBeVisible();
    await expect(option.locator('img')).toHaveCount(0);
    await option.click();
    await expect(page.locator('#CategoryId')).toHaveValue(String(category.id));
    await expect(page.getByRole('combobox', { name: /^Category/ })).toContainText(name);
    expect(await page.evaluate(() => window.qa2Injected)).toBeUndefined();
});

test('[F2-07] network failure preserves selection and a later search recovers', async ({ page, data }) => {
    const recipe = await data.recipe();
    await page.goto('/Recipes/Edit/' + recipe.id);
    await page.route('**/Recipes/CategoryOptions?*', route => route.abort('failed'));
    const input = await search(page, 'Des');
    await expect(page.locator('.select2-results')).toContainText('could not be loaded');
    await expect(page.locator('#CategoryId')).toHaveValue('1');
    await page.unroute('**/Recipes/CategoryOptions?*');
    await input.fill('');
    await input.pressSequentially('Dessert');
    await page.getByRole('option', { name: 'Dessert', exact: true }).click();
    await expect(page.locator('#CategoryId')).toHaveValue('3');
});

test('[F2-08] a delayed old response cannot replace the short-input hint', async ({ page }) => {
    let release;
    let reached;
    const ready = new Promise(resolve => { reached = resolve; });
    const gate = new Promise(resolve => { release = resolve; });
    await page.route('**/Recipes/CategoryOptions?*', async route => {
        const response = await route.fetch(); // Real backend data; only delivery is delayed.
        reached();
        await gate;
        await route.fulfill({ response });
    });
    try {
        await page.goto('/Recipes/Create');
        const input = await search(page, 'Salad');
        await ready;
        await input.fill('Sa');
        await expect(page.locator('.select2-results')).toContainText('1 or more characters');
        const delivered = page.waitForResponse(lookup);
        release();
        await delivered;
        await expect(page.getByRole('option', { name: 'Salad', exact: true })).toHaveCount(0);
        await expect(page.locator('.select2-results')).toContainText('1 or more characters');
    } finally { release(); }
});

test('[F2-09] missing library and JavaScript-disabled edit show fallback guidance', async ({ page, browser, app, data }) => {
    await page.route('**/lib/select2/select2.min.js?*', route => route.abort('failed'));
    await page.goto('/Recipes/Create');
    await expect(page.locator('#category-search-status')).toBeVisible();
    await expect(page.locator('#category-search-status')).toContainText('reload');
    const recipe = await data.recipe({ title: 'QA2 no JavaScript edit' });
    const context = await browser.newContext({ baseURL: app.baseURL, javaScriptEnabled: false });
    try {
        await signIn(context.request);
        const plain = await context.newPage();
        await plain.goto('/Recipes/Edit/' + recipe.id);
        await expect(plain.locator('#category-search-status')).toBeVisible();
        await expect(plain.locator('#CategoryId')).toHaveValue('1');
        await plain.getByLabel('Title', { exact: true }).fill('QA2 edited without JavaScript');
        await plain.getByRole('button', { name: 'Save', exact: true }).click();
        await expect(plain).toHaveURL(/\/Recipes$/);
        await plain.goto('/Recipes/Details/' + recipe.id);
        await expect(plain.getByRole('heading', { name: 'QA2 edited without JavaScript', exact: true })).toBeVisible();
        await expect(plain.locator('.recipe-category')).toHaveText('Breakfast');
    } finally { await context.close(); }
});

test('[F2-10] long labels and open results fit desktop and mobile forms', async ({ page, data }, testInfo) => {
    const name = 'Ж'.repeat(100);
    await data.category(name);
    for (const [width, height] of [[1280, 900], [375, 812], [320, 740]]) {
        await page.setViewportSize({ width, height });
        await page.goto('/Recipes/Create');
        await search(page, name);
        await expect(page.getByRole('option', { name, exact: true })).toBeVisible();
        expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
        await page.getByRole('option', { name, exact: true }).click();
        expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
        await page.screenshot({ path: testInfo.outputPath(`form-${width}.png`), fullPage: true });
    }
});
