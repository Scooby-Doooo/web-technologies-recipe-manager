import { randomBytes } from 'node:crypto';
import { writeFileSync } from 'node:fs';
import { Agent, request as httpRequest } from 'node:http';
import { connect } from 'node:net';
import { test, expect, startExtraServer } from '../fixtures.js';
import { kitchenUser } from '../helpers.js';

// C10 comparison. Not part of the normal run: LIVE_BENCHMARK=1 npm test -- specs/live-benchmark.spec.js
// The four clients below behave like live.js but count every byte on the wire: request line and
// headers (with the real sign-in cookie), response headers and body, WebSocket handshake and frames.
test.skip(!process.env.LIVE_BENCHMARK, 'Set LIVE_BENCHMARK=1 to run the transport comparison.');
test.setTimeout(10 * 60_000);

const browserHeaders = {
    Accept: 'application/json',
    'User-Agent': 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/142.0.0.0 Safari/537.36',
    'Accept-Encoding': 'gzip, deflate, br, zstd',
    'Accept-Language': 'uk-UA,uk;q=0.9,en;q=0.8'
};
const pause = milliseconds => new Promise(resolve => setTimeout(resolve, milliseconds));
const lineBytes = lines => Buffer.byteLength(lines.join('\r\n') + '\r\n\r\n');

function newStats(name) {
    return { name, requests: 0, empty: 0, events: 0, up: 0, down: 0, delays: [] };
}

function receive(stats, text) {
    const data = JSON.parse(text);
    stats.requests++;
    if (!data.events.length) stats.empty++;
    for (const event of data.events) {
        stats.events++;
        stats.delays.push(Date.now() - event.at);
    }
    return data;
}

// HTTP/1.1 with keep-alive, one connection per client, as a browser tab would use.
function httpClient(baseURL, cookie) {
    const url = new URL(baseURL);
    const agent = new Agent({ keepAlive: true, maxSockets: 1 });
    const headers = { Host: url.host, Connection: 'keep-alive', ...browserHeaders, Referer: baseURL + '/Live', Cookie: cookie };
    return path => new Promise((resolve, reject) => {
        const request = httpRequest({ host: url.hostname, port: url.port, path, agent, headers }, response => {
            let body = '';
            response.setEncoding('utf8');
            response.on('data', chunk => { body += chunk; });
            response.on('end', () => {
                const head = [`HTTP/1.1 ${response.statusCode} ${response.statusMessage}`];
                for (let i = 0; i < response.rawHeaders.length; i += 2) head.push(`${response.rawHeaders[i]}: ${response.rawHeaders[i + 1]}`);
                resolve({ body, up: lineBytes([`GET ${path} HTTP/1.1`, ...Object.entries(headers).map(([key, value]) => `${key}: ${value}`)]),
                    down: lineBytes(head) + Buffer.byteLength(body) });
            });
        });
        request.on('error', reject);
        request.end();
    });
}

async function pollingClient(stats, get, running, adaptive, since) {
    let interval = 1000;
    while (running()) {
        const answer = await get(`/Live/Events?since=${since}`);
        stats.up += answer.up;
        stats.down += answer.down;
        const data = receive(stats, answer.body);
        since = data.last;
        interval = !adaptive || data.events.length ? 1000 : Math.min(interval * 2, 16000);
        await pause(interval);
    }
}

async function longPollingClient(stats, get, running, since) {
    while (running()) {
        const answer = await get(`/Live/Wait?since=${since}`);
        stats.up += answer.up;
        stats.down += answer.down;
        since = receive(stats, answer.body).last;
    }
}

// A raw WebSocket client: the handshake, server frames and pong replies are all counted.
function webSocketClient(stats, baseURL, cookie, since) {
    const url = new URL(baseURL);
    const socket = connect(Number(url.port), url.hostname);
    const handshake = [`GET /Live/Socket?since=${since} HTTP/1.1`, `Host: ${url.host}`, 'Connection: Upgrade', 'Upgrade: websocket',
        'Sec-WebSocket-Version: 13', `Sec-WebSocket-Key: ${randomBytes(16).toString('base64')}`, `Origin: ${baseURL}`,
        `User-Agent: ${browserHeaders['User-Agent']}`, `Cookie: ${cookie}`].join('\r\n') + '\r\n\r\n';
    socket.write(handshake);
    stats.up += Buffer.byteLength(handshake);
    let buffer = Buffer.alloc(0);
    let upgraded = false;
    socket.on('data', chunk => {
        stats.down += chunk.length;
        buffer = Buffer.concat([buffer, chunk]);
        if (!upgraded) {
            const end = buffer.indexOf('\r\n\r\n');
            if (end < 0) return;
            expect(buffer.subarray(0, 12).toString()).toBe('HTTP/1.1 101');
            buffer = buffer.subarray(end + 4);
            upgraded = true;
        }
        while (buffer.length >= 2) {
            const opcode = buffer[0] & 0x0f;
            let length = buffer[1] & 0x7f;
            let offset = 2;
            if (length === 126) {
                if (buffer.length < 4) return;
                length = buffer.readUInt16BE(2);
                offset = 4;
            }
            if (buffer.length < offset + length) return;
            const payload = buffer.subarray(offset, offset + length);
            buffer = buffer.subarray(offset + length);
            if (opcode === 1) receive(stats, payload.toString());
            if (opcode === 9) {
                // Clients mask what they send: 2 header bytes, 4 mask bytes, then the masked payload.
                const mask = randomBytes(4);
                const pong = Buffer.concat([Buffer.from([0x8a, 0x80 | payload.length]), mask, payload.map((byte, i) => byte ^ mask[i % 4])]);
                socket.write(pong);
                stats.up += pong.length;
            }
        }
    });
    return () => socket.destroy();
}

function summary(stats, expected, seconds) {
    const sorted = [...stats.delays].sort((a, b) => a - b);
    const average = sorted.length ? Math.round(sorted.reduce((sum, value) => sum + value, 0) / sorted.length) : null;
    return {
        transport: stats.name,
        requests: stats.requests,
        empty: stats.empty,
        events: `${stats.events}/${expected}`,
        requestsPerMinute: Math.round(stats.requests / seconds * 60),
        kbDown: +(stats.down / 1024).toFixed(1),
        kbUp: +(stats.up / 1024).toFixed(1),
        bytesPerEvent: stats.events ? Math.round((stats.up + stats.down) / stats.events) : null,
        delayAverageMs: average,
        delayP95Ms: sorted.length ? sorted[Math.min(sorted.length - 1, Math.floor(sorted.length * 0.95))] : null,
        delayMaxMs: sorted.at(-1) ?? null
    };
}

test('C10 transport comparison', async ({ browser, app }) => {
    // Its own server keeps the default 25 s long poll; the shared test server uses 2 s.
    const server = await startExtraServer(app.directory, 'live-benchmark', 'Development', { Authentication__EnableTestSignIn: 'true' });
    const user = await kitchenUser(browser, { baseURL: server.baseURL }, 'benchmark');
    const results = {};
    try {
        const [category] = await (await user.api.get('/api/categories')).json();
        const body = title => ({ title, instructions: 'Stir.', cookingTimeMinutes: 10, servings: 2, categoryId: category.id });
        const recipe = await (await user.api.post('/api/recipes', body('QA benchmark'))).json();
        const cookie = (await user.context.cookies()).map(item => `${item.name}=${item.value}`).join('; ');

        for (const workload of [{ name: 'quiet: 1 change every 10 s', every: 10_000 }, { name: 'busy: 2 changes per second', every: 500 }]) {
            const duration = 60_000;
            const drain = 17_000; // the slowest adaptive pause is 16 s
            let running = true;
            // Clients start after the current events, like live.js after loading the history.
            const { last } = await (await user.api.get('/Live/Events?since=0')).json();
            const clients = ['polling 1 s', 'adaptive 1–16 s', 'long polling', 'WebSocket'].map(newStats);
            const started = Date.now();
            const loops = [
                pollingClient(clients[0], httpClient(server.baseURL, cookie), () => running, false, last),
                pollingClient(clients[1], httpClient(server.baseURL, cookie), () => running, true, last),
                longPollingClient(clients[2], httpClient(server.baseURL, cookie), () => running, last)
            ];
            const closeSocket = webSocketClient(clients[3], server.baseURL, cookie, last);
            await pause(1000); // every client has its first answer before the first change
            let changes = 0;
            while (Date.now() - started < duration) {
                await user.api.put('/api/recipes/' + recipe.id, body(`QA benchmark ${++changes}`));
                await pause(workload.every);
            }
            await pause(drain);
            running = false;
            closeSocket();
            const seconds = (Date.now() - started) / 1000;
            results[workload.name] = clients.map(stats => summary(stats, changes, seconds));
            // An open long poll ends within 25 s; the counted numbers are taken before that.
            await Promise.allSettled(loops);
            console.log(`\n${workload.name}, ${changes} changes, ${Math.round(seconds)} s`);
            console.table(results[workload.name]);
            for (const stats of clients) expect(stats.events, stats.name).toBe(changes);
        }
        writeFileSync(new URL('../test-results/live-benchmark.json', import.meta.url), JSON.stringify(results, null, 2));
    } finally {
        await user.context.close();
        await server.close();
    }
});
