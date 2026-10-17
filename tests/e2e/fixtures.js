import { test as base, expect } from '@playwright/test';
import { spawn, spawnSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { closeSync, cpSync, existsSync, mkdirSync, mkdtempSync, openSync, readdirSync, readFileSync, writeFileSync } from 'node:fs';
import { createServer } from 'node:net';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { DatabaseSync } from 'node:sqlite';
import { fileURLToPath } from 'node:url';
import { startFakeMealDb } from './fake-mealdb.js';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..');
const project = path.join(root, 'src/RecipeManager.Web');

function protectedFiles() {
    const files = [path.join(root, 'README.md')];
    for (const name of readdirSync(project)) {
        if (/\.db(?:-(?:wal|shm|journal))?$/.test(name)) files.push(path.join(project, name));
    }
    const uploads = path.join(project, 'wwwroot/uploads/recipes');
    const visit = directory => {
        if (!existsSync(directory)) return;
        for (const entry of readdirSync(directory, { withFileTypes: true })) {
            const target = path.join(directory, entry.name);
            if (entry.isDirectory()) visit(target);
            else files.push(target);
        }
    };
    visit(uploads);
    return Object.fromEntries(files.sort().map(file => [path.relative(root, file), createHash('sha256').update(readFileSync(file)).digest('hex')]));
}

function command(args) {
    const result = spawnSync('dotnet', args, { cwd: project, encoding: 'utf8', windowsHide: true });
    if (result.status !== 0) throw new Error(`dotnet ${args[0]} failed. Build the solution and install dotnet-ef 10.0.12 before testing.\n${result.stderr || result.stdout}`);
}

async function freePort() {
    const server = createServer();
    await new Promise((resolve, reject) => { server.once('error', reject); server.listen(0, '127.0.0.1', resolve); });
    const port = server.address().port;
    await new Promise(resolve => server.close(resolve));
    return port;
}

async function stop(child) {
    if (!child || child.exitCode !== null) return;
    const closed = new Promise(resolve => child.once('close', resolve));
    child.kill(); // The owned dotnet process is launched directly, without a shell or shared server.
    await closed;
}

// Starts one more isolated server next to the shared one, for checks that need other settings
// (Production, a short token lifetime). It creates its own database through the startup migration.
export async function startExtraServer(directory, name, environment, settings = {}) {
    const dll = path.join(project, 'bin/Debug/net10.0/RecipeManager.Web.dll');
    const baseURL = `http://127.0.0.1:${await freePort()}`;
    const env = { ...process.env, ASPNETCORE_ENVIRONMENT: environment,
        ConnectionStrings__DefaultConnection: `Data Source=${path.join(directory, name + '.db')}`,
        Database__MigrateOnStartup: 'true',
        DataProtection__KeysPath: path.join(directory, name + '-keys'),
        Analytics__MeasurementId: '',
        Authentication__Google__ClientId: '',
        Telegram__BotToken: '',
        OpenRouter__ApiKey: '',
        // Nothing listens on port 9, so an extra server never reaches the real TheMealDB.
        TheMealDb__ApiBaseUrl: 'http://127.0.0.1:9/',
        Logging__LogLevel__Default: 'Warning', ...settings };
    delete env.ASPNETCORE_HTTPS_PORT;
    delete env.ASPNETCORE_HTTPS_PORTS;
    const log = openSync(path.join(directory, name + '.log'), 'w');
    const child = spawn('dotnet', [dll, '--urls', baseURL, '--contentRoot', project], {
        cwd: project, env, stdio: ['ignore', log, log], windowsHide: true
    });
    const close = async () => { await stop(child); closeSync(log); };
    try {
        await expect.poll(async () => {
            if (child.exitCode !== null) throw new Error(`Extra ${name} server exited. See ${name}.log.`);
            try { return (await fetch(baseURL + '/Categories')).status; } catch { return 0; }
        }, { timeout: 20_000, message: `Extra ${name} server starts` }).toBe(200);
    } catch (error) { await close(); throw error; }
    return { baseURL, close };
}

// Uses the Development-only test sign-in, so the suite never needs a Google account.
export async function signIn(request, email = 'qa@example.test') {
    const response = await request.post('/Account/TestSignIn', { params: { email } });
    expect(response.status(), 'Test sign-in is enabled for the owned server').toBe(204);
}

export const test = base.extend({
    app: [async ({}, use) => {
        const before = protectedFiles();
        const directory = mkdtempSync(path.join(tmpdir(), 'recipe-manager-qa2-'));
        const database = path.join(directory, 'test.db');
        const webroot = path.join(directory, 'wwwroot');
        cpSync(path.join(project, 'wwwroot'), webroot, {
            recursive: true,
            filter: source => source !== path.join(project, 'wwwroot/uploads')
        });
        mkdirSync(path.join(webroot, 'uploads/recipes'), { recursive: true });
        const dll = path.join(project, 'bin/Debug/net10.0/RecipeManager.Web.dll');
        if (!existsSync(dll)) throw new Error('Run dotnet restore/build WebTechnologies.sln before npm test.');
        let child;
        let log;
        let baseURL;
        const mealDb = await startFakeMealDb();
        try {
            command(['ef', 'database', 'update', '--no-build', '--project', project, '--connection', `Data Source=${database}`]);
            baseURL = `http://127.0.0.1:${await freePort()}`;
            const env = { ...process.env, ASPNETCORE_ENVIRONMENT: 'Development',
                ConnectionStrings__DefaultConnection: `Data Source=${database}`,
                Analytics__MeasurementId: 'G-QA2TEST000',
                Authentication__EnableTestSignIn: 'true',
                // Development loads the developer's user secrets; the suite never uses a real Google
                // client, a real Telegram bot or a real OpenRouter key.
                Authentication__Google__ClientId: '',
                Telegram__BotToken: '',
                OpenRouter__ApiKey: '',
                TheMealDb__ApiBaseUrl: mealDb.url,
                // A small Free plan limit keeps the B6 limit test fast; the public kitchen is Premium.
                Plans__FreeRecipeLimit: '3',
                Logging__LogLevel__Default: 'Warning' };
            delete env.ASPNETCORE_HTTPS_PORT;
            delete env.ASPNETCORE_HTTPS_PORTS;
            log = openSync(path.join(directory, 'application.log'), 'w');
            child = spawn('dotnet', [dll, '--urls', baseURL, '--contentRoot', project, '--webroot', webroot], {
                cwd: project, env, stdio: ['ignore', log, log], windowsHide: true
            });
            let spawnError;
            child.once('error', error => { spawnError = error; });
            await expect.poll(async () => {
                if (spawnError) throw spawnError;
                if (child.exitCode !== null) throw new Error('Owned test server exited. See temporary application.log.');
                try { return (await fetch(baseURL + '/Categories')).status; } catch { return 0; }
            }, { timeout: 20_000, message: 'Fresh isolated MVC server starts' }).toBe(200);
            await use({ baseURL, directory, database, mealDb });
        } finally {
            await stop(child);
            await mealDb.close();
            if (log !== undefined) closeSync(log);
            const preserved = JSON.stringify(protectedFiles()) === JSON.stringify(before);
            writeFileSync(path.join(directory, 'isolation.json'), JSON.stringify({
                baseURL, ownedProcessId: child?.pid, protectedFileCount: Object.keys(before).length,
                originalDatabaseUploadsReadmeUnchanged: preserved, ownedProcessStopped: true
            }, null, 2));
            console.log(`Isolated QA2 artifacts: ${directory}`);
            expect(preserved, 'Working database, uploads and README remain unchanged').toBe(true);
        }
    }, { scope: 'worker', timeout: 60_000 }],
    baseURL: async ({ app }, use) => { await use(app.baseURL); },
    page: async ({ page }, use, testInfo) => {
        const errors = [];
        const observations = [];
        page.on('pageerror', error => errors.push(error.message));
        // Tests must not report visits to the real Google Analytics property.
        await page.route('https://www.googletagmanager.com/**', route => route.fulfill({ contentType: 'text/javascript', body: '' }));
        await signIn(page.request);
        page.on('response', response => {
            const url = new URL(response.url());
            if (/^\/(Recipes|Categories|Ingredients)(\/|$)/.test(url.pathname)) {
                observations.push({ method: response.request().method(),
                    path: url.pathname.replace(/\/\d+$/, '/:id'), status: response.status() });
            }
        });
        await use(page);
        await testInfo.attach('route-observations', { body: Buffer.from(JSON.stringify(observations)), contentType: 'application/json' });
        expect(errors, 'No JavaScript page exceptions').toEqual([]);
    },
    data: async ({ request, app }, use) => {
        await signIn(request);
        const tokenResponse = await request.get('/api/antiforgery');
        expect(tokenResponse.status()).toBe(200);
        const { requestToken, headerName } = await tokenResponse.json();
        const headers = { [headerName]: requestToken }; // Kept in memory; never attached to reports.
        const owned = { recipes: [], categories: [], ingredients: [] };
        const create = async (kind, fields) => {
            const response = await request.post('/api/' + kind, { headers, data: fields });
            expect(response.status()).toBe(201);
            const record = await response.json();
            owned[kind].push(record.id);
            return record;
        };
        await use({
            category: name => create('categories', { name }),
            ingredient: name => create('ingredients', { name }),
            recipe: (fields = {}) => create('recipes', { title: 'QA2 fixture recipe', instructions: 'Prepare and serve.',
                cookingTimeMinutes: 20, servings: 2, categoryId: 1, ...fields }),
            link: (recipeId, ingredientId) => {
                const db = new DatabaseSync(app.database);
                try {
                    db.exec('PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;');
                    db.prepare('INSERT INTO RecipeIngredients(RecipeId,IngredientId,Quantity,Unit) VALUES(?,?,?,?)').run(recipeId, ingredientId, 2, 'pieces');
                } finally { db.close(); }
            }
        });
        for (const kind of ['recipes', 'categories', 'ingredients']) {
            for (const id of owned[kind]) {
                const response = await request.delete(`/api/${kind}/${id}`, { headers });
                expect([204, 404]).toContain(response.status()); // UI CRUD tests may already have removed the record.
            }
        }
    }
});

export { expect };
