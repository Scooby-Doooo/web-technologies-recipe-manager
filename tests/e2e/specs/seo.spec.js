import { test, expect } from '../fixtures.js';
import { png } from '../helpers.js';

const meta = (page, selector) => page.locator(`head ${selector}`).getAttribute('content');

test('[SEO-01] recipe landing exposes meta tags and schema.org Recipe data', async ({ page, data, request, baseURL }) => {
    const recipe = await data.recipe({ title: 'QA2 SEO pasta', description: 'Pasta with tomatoes </script> and herbs.',
        instructions: 'Boil the water.\n\nCook the pasta.\nServe warm.', cookingTimeMinutes: 25, servings: 4 });
    const ingredient = await data.ingredient('QA2 SEO tomato');
    data.link(recipe.id, ingredient.id);
    const { requestToken, headerName } = await (await request.get('/api/antiforgery')).json();
    const upload = await request.post(`/api/recipes/${recipe.id}/image`, {
        headers: { [headerName]: requestToken },
        multipart: { imageFile: { name: 'seo.png', mimeType: 'image/png', buffer: png } }
    });
    expect(upload.status()).toBe(200);
    const { imagePath } = await upload.json();

    const url = `${baseURL}/Recipes/Details/${recipe.id}`;
    await page.goto('/Recipes/Details/' + recipe.id);
    await expect(page).toHaveTitle('QA2 SEO pasta - Recipe Manager');
    await expect(page.locator('h1')).toHaveCount(1);
    expect(await meta(page, 'meta[name="description"]')).toBe('Pasta with tomatoes </script> and herbs.');
    expect(await meta(page, 'meta[name="robots"]')).toBe('index, follow');
    expect(await page.locator('head link[rel="canonical"]').getAttribute('href')).toBe(url);
    expect(await meta(page, 'meta[property="og:type"]')).toBe('article');
    expect(await meta(page, 'meta[property="og:url"]')).toBe(url);
    expect(await meta(page, 'meta[property="og:image"]')).toBe(baseURL + imagePath);

    const scripts = await page.locator('script[type="application/ld+json"]').allTextContents();
    expect(scripts).toHaveLength(1);
    const schema = JSON.parse(scripts[0]);
    expect(schema).toMatchObject({
        '@context': 'https://schema.org', '@type': 'Recipe', name: 'QA2 SEO pasta', url,
        image: [baseURL + imagePath], recipeCategory: 'Breakfast', cookTime: 'PT25M', recipeYield: '4 servings',
        recipeIngredient: ['2 pieces QA2 SEO tomato']
    });
    expect(schema.recipeInstructions.map(step => step.text)).toEqual(['Boil the water.', 'Cook the pasta.', 'Serve warm.']);
});

test('[SEO-02] robots.txt, sitemap.xml and noindex forms guide crawlers', async ({ page, data, request, baseURL }) => {
    const recipe = await data.recipe({ title: 'QA2 sitemap recipe' });
    const robots = await request.get('/robots.txt');
    expect(robots.status()).toBe(200);
    const robotsText = await robots.text();
    expect(robotsText).toContain('Disallow: /api/');
    expect(robotsText).toContain(`Sitemap: ${baseURL}/sitemap.xml`);

    const sitemap = await request.get('/sitemap.xml');
    expect(sitemap.headers()['content-type']).toContain('application/xml');
    const sitemapText = await sitemap.text();
    expect(sitemapText).toContain('<urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">');
    expect(sitemapText).toContain(`<loc>${baseURL}/Recipes/Details/${recipe.id}</loc>`);

    for (const path of ['/Recipes/Create', `/Recipes/Edit/${recipe.id}`, `/Recipes/Delete/${recipe.id}`]) {
        await page.goto(path);
        expect(await meta(page, 'meta[name="robots"]')).toBe('noindex, follow');
        await expect(page.locator('head link[rel="canonical"]')).toHaveCount(0);
    }
    for (const path of ['/', '/Recipes', '/Statistics']) {
        await page.goto(path);
        expect(await meta(page, 'meta[name="robots"]')).toBe('index, follow');
        expect((await meta(page, 'meta[name="description"]')).length).toBeGreaterThan(50);
    }
});
