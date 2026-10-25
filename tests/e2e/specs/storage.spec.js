import { createServer } from 'node:http';
import { test, expect, startExtraServer } from '../fixtures.js';
import { kitchenUser, png, save } from '../helpers.js';

const secret = 'sb_secret_qa-only-test-key';

// A stand-in for Supabase Storage: the object API the server writes to and the public URLs
// browsers read from. It records every call and can be told to fail.
async function fakeStorage() {
    const requests = [];
    const objects = new Map();
    const state = { failing: false };
    const server = createServer((request, response) => {
        const chunks = [];
        request.on('data', chunk => chunks.push(chunk));
        request.on('end', () => {
            const path = new URL(request.url, 'http://fake').pathname;
            const body = Buffer.concat(chunks);
            requests.push({ method: request.method, path, headers: request.headers, body });
            const publicPath = path.match(/^\/storage\/v1\/object\/public\/recipe-photos\/([^/]+)$/);
            const objectPath = path.match(/^\/storage\/v1\/object\/recipe-photos\/([^/]+)$/);
            if (request.method === 'GET' && publicPath && objects.has(publicPath[1])) {
                const object = objects.get(publicPath[1]);
                response.writeHead(200, { 'Content-Type': object.type, 'Access-Control-Allow-Origin': '*' });
                return response.end(object.body);
            }
            const authorized = request.headers.apikey === secret && request.headers.authorization === 'Bearer ' + secret;
            if (state.failing || !authorized || !objectPath) {
                response.writeHead(state.failing ? 500 : 400, { 'Content-Type': 'application/json' });
                return response.end('{"error":"refused"}');
            }
            if (request.method === 'POST') objects.set(objectPath[1], { type: request.headers['content-type'], body });
            if (request.method === 'DELETE') objects.delete(objectPath[1]);
            response.writeHead(200, { 'Content-Type': 'application/json' });
            response.end('{}');
        });
    });
    await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
    const url = `http://127.0.0.1:${server.address().port}`;
    return { url, requests, objects, state, close: () => new Promise(resolve => server.close(resolve)) };
}

test('[C19-01] Photos go to Supabase Storage, are shown from there and are removed with the recipe', async ({ browser, app }) => {
    const storage = await fakeStorage();
    const server = await startExtraServer(app.directory, 'storage', 'Development', {
        Authentication__EnableTestSignIn: 'true',
        Supabase__Url: storage.url,
        Supabase__SecretKey: secret
    });
    const user = await kitchenUser(browser, { baseURL: server.baseURL }, 'storage');
    try {
        const [category] = await (await user.api.get('/api/categories')).json();
        const recipe = await (await user.api.post('/api/recipes', { title: 'QA cloud photo pie', instructions: 'Bake.',
            cookingTimeMinutes: 30, servings: 4, categoryId: category.id })).json();
        const { requestToken, headerName } = await (await user.context.request.get('/api/antiforgery')).json();
        const upload = () => user.context.request.post(`/api/recipes/${recipe.id}/image`, {
            headers: { [headerName]: requestToken },
            multipart: { imageFile: { name: 'photo.png', mimeType: 'image/png', buffer: png } }
        });

        // The server sends the file with the secret key; the recipe keeps the public URL.
        const uploaded = await upload();
        expect(uploaded.status()).toBe(200);
        const first = (await uploaded.json()).imagePath;
        expect(first).toMatch(new RegExp(`^${storage.url}/storage/v1/object/public/recipe-photos/[0-9a-f]{32}\\.png$`));
        const name = first.split('/').at(-1);
        const sent = storage.requests.find(item => item.method === 'POST');
        expect(sent.path).toBe('/storage/v1/object/recipe-photos/' + name);
        expect(sent.headers['content-type']).toBe('image/png');
        expect(sent.body.equals(png)).toBe(true);

        // The page shows the photo straight from Storage, also in Open Graph; the key never reaches the browser.
        const { page } = user;
        await page.goto('/Recipes/Details/' + recipe.id);
        const image = page.getByRole('img', { name: 'Image for QA cloud photo pie', exact: true });
        await expect(image).toHaveAttribute('src', first);
        expect(await image.evaluate(element => element.complete && element.naturalWidth > 0)).toBe(true);
        await expect(page.locator('meta[property="og:image"]')).toHaveAttribute('content', first);
        expect(await page.content()).not.toContain(secret);

        // A new photo from the edit form replaces the old one, which is removed from Storage.
        await page.goto('/Recipes/Edit/' + recipe.id);
        await page.getByLabel('Recipe image (optional)').setInputFiles({ name: 'second.png', mimeType: 'image/png', buffer: png });
        await save(page);
        const second = (await (await user.api.get('/api/recipes/' + recipe.id)).json()).imagePath;
        expect(second).not.toBe(first);
        expect(storage.objects.has(name)).toBe(false);
        expect(storage.objects.size).toBe(1);

        // When Storage fails, the API says so and the recipe keeps its photo.
        storage.state.failing = true;
        expect((await upload()).status()).toBe(500);
        expect((await (await user.api.get('/api/recipes/' + recipe.id)).json()).imagePath).toBe(second);
        storage.state.failing = false;

        // Deleting the recipe deletes its photo.
        expect((await user.api.delete('/api/recipes/' + recipe.id)).status()).toBe(204);
        await expect.poll(() => storage.objects.size).toBe(0);
    } finally {
        await user.context.close();
        await server.close();
        await storage.close();
    }
});
