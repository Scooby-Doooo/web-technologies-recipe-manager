// Live kitchen page (C10): one feed of recipe changes, delivered by polling every second,
// adaptive polling, long polling or a WebSocket. Every way ends in receive(), so the numbers
// on the page compare like with like.
(() => {
    const root = document.getElementById('live');
    if (!root) return;
    const urls = root.dataset;
    const feed = document.getElementById('live-feed');
    const emptyFeed = document.getElementById('live-empty-feed');
    const shown = Object.fromEntries(['requests', 'empty', 'events', 'bytes', 'average', 'state']
        .map(name => [name, document.getElementById('live-' + name)]));
    const labels = { created: 'Recipe added', updated: 'Recipe changed', deleted: 'Recipe deleted' };
    const encoder = new TextEncoder();
    let since = 0;
    let stats = null;
    let stop = () => {};

    function element(tag, className, text) {
        const node = document.createElement(tag);
        if (className) node.className = className;
        if (text !== undefined) node.textContent = text;
        return node;
    }

    const size = bytes => bytes < 1024 ? `${bytes} B` : `${(bytes / 1024).toFixed(1)} KB`;
    const setState = text => { shown.state.textContent = text; };

    function render() {
        shown.requests.textContent = stats.requests;
        shown.empty.textContent = stats.empty;
        shown.events.textContent = stats.events;
        shown.bytes.textContent = size(stats.bytes);
        shown.average.textContent = stats.events ? `${Math.round(stats.delay / stats.events)} ms` : '–';
    }

    function add(event) {
        const item = element('li', 'live-event');
        item.append(element('span', 'recipe-muted', new Date(event.at).toLocaleTimeString() + ' '),
            element('span', 'live-type', labels[event.type] || event.type), ' ');
        if (event.type === 'deleted') {
            item.append(element('span', null, event.title));
        } else {
            const link = element('a', null, event.title);
            link.href = `${urls.detailsUrl}/${event.recipeId}`;
            item.append(link);
        }
        feed.prepend(item);
        while (feed.children.length > 50) feed.lastChild.remove();
        emptyFeed.hidden = true;
    }

    // One answer of any transport. The delay is measured from the save on the server to now.
    function receive(text) {
        const data = JSON.parse(text);
        stats.requests++;
        stats.bytes += encoder.encode(text).length;
        if (!data.events.length) stats.empty++;
        for (const event of data.events) {
            stats.events++;
            stats.delay += Math.max(0, Date.now() - event.at);
            add(event);
        }
        since = data.last;
        render();
        return data.events.length > 0;
    }

    async function get(url, signal) {
        const response = await fetch(url, { signal, headers: { Accept: 'application/json' } });
        if (!response.ok) throw new Error(`The server answered ${response.status}.`);
        return response.text();
    }

    const pause = milliseconds => new Promise(resolve => setTimeout(resolve, milliseconds));

    // Polling asks every second. Adaptive polling doubles the pause after each empty answer,
    // up to 16 s, and goes back to 1 s as soon as something changes.
    function polling(adaptive) {
        const controller = new AbortController();
        (async () => {
            let interval = 1000;
            while (!controller.signal.aborted) {
                try {
                    const changed = receive(await get(`${urls.eventsUrl}?since=${since}`, controller.signal));
                    interval = !adaptive || changed ? 1000 : Math.min(interval * 2, 16000);
                    setState(adaptive ? `Next request in ${interval / 1000} s` : 'Polling');
                } catch (error) {
                    if (error.name === 'AbortError') return;
                    setState('Connection lost, retrying');
                }
                await pause(interval);
            }
        })();
        return () => controller.abort();
    }

    // The server holds each request until something changes, so the next one starts right away.
    function longPolling() {
        const controller = new AbortController();
        (async () => {
            while (!controller.signal.aborted) {
                try {
                    setState('Waiting for a change');
                    receive(await get(`${urls.waitUrl}?since=${since}`, controller.signal));
                } catch (error) {
                    if (error.name === 'AbortError') return;
                    setState('Connection lost, retrying');
                    await pause(2000);
                }
            }
        })();
        return () => controller.abort();
    }

    function webSocket() {
        let closed = false;
        let socket = null;
        let retry = null;
        const connect = () => {
            const url = new URL(`${urls.socketUrl}?since=${since}`, location.href);
            url.protocol = url.protocol === 'https:' ? 'wss:' : 'ws:';
            socket = new WebSocket(url);
            socket.onopen = () => setState('Connected');
            socket.onmessage = message => receive(message.data);
            socket.onclose = () => {
                if (closed) return;
                setState('Connection lost, retrying');
                retry = setTimeout(connect, 2000);
            };
        };
        connect();
        return () => {
            closed = true;
            clearTimeout(retry);
            socket.close();
        };
    }

    const transports = { polling: () => polling(false), adaptive: () => polling(true), long: longPolling, socket: webSocket };

    function start(name) {
        stop();
        stats = { requests: 0, empty: 0, events: 0, bytes: 0, delay: 0 };
        render();
        stop = transports[name]();
    }

    root.addEventListener('change', event => {
        if (event.target.name === 'transport') start(event.target.value);
    });

    // The recent history is loaded once and is not counted in the statistics.
    (async () => {
        try {
            const data = JSON.parse(await get(`${urls.eventsUrl}?since=0`));
            data.events.forEach(add);
            since = data.last;
        } catch {
            // The chosen transport retries on its own.
        }
        start(root.querySelector('input[name="transport"]:checked').value);
    })();
})();
