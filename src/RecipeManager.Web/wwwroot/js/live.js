// Live page: the same feed through polling, adaptive polling, long polling, WebSocket or SignalR.
// All of them end in receive(), so the numbers are comparable.
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
    // SignalR hands over parsed objects, so its size is the JSON of the object.
    function receive(text) {
        const data = typeof text === 'string' ? JSON.parse(text) : text;
        stats.requests++;
        stats.bytes += encoder.encode(typeof text === 'string' ? text : JSON.stringify(text)).length;
        // A push can repeat what the catch-up after (re)connecting already returned; a lower
        // "last" means the server restarted and numbering began again.
        if (data.last < since) since = 0;
        const fresh = data.events.filter(event => event.id > since);
        if (!fresh.length) stats.empty++;
        for (const event of fresh) {
            stats.events++;
            stats.delay += Math.max(0, Date.now() - event.at);
            add(event);
        }
        since = Math.max(since, data.last);
        render();
        return fresh.length > 0;
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

    // SignalR reconnects by itself; after each (re)connect the hub returns what was missed.
    function signalR() {
        const chosen = document.getElementById('signalr-transport').value;
        const options = chosen === 'auto' ? {} : { transport: window.signalR.HttpTransportType[chosen] };
        const connection = new window.signalR.HubConnectionBuilder()
            .withUrl(urls.hubUrl, options)
            .withAutomaticReconnect()
            .build();
        const catchUp = async () => receive(await connection.invoke('Since', since));
        connection.on('changes', receive);
        connection.onreconnecting(() => setState('Connection lost, retrying'));
        let stopped = false;
        // withAutomaticReconnect gives up after attempts at 0, 2, 10 and 30 s.
        connection.onclose(() => {
            if (!stopped) setState('Disconnected. Reload the page to connect again.');
        });
        connection.onreconnected(async () => {
            setState('Connected');
            await catchUp();
        });
        (async () => {
            while (!stopped) {
                try {
                    await connection.start();
                    if (stopped) return connection.stop();
                    setState('Connected');
                    await catchUp();
                    return;
                } catch {
                    if (stopped) return;
                    setState('Connection lost, retrying');
                    await pause(2000);
                }
            }
        })();
        return () => {
            stopped = true;
            connection.stop();
        };
    }

    const transports = { polling: () => polling(false), adaptive: () => polling(true), long: longPolling, socket: webSocket, signalr: signalR };

    function start(name) {
        stop();
        stats = { requests: 0, empty: 0, events: 0, bytes: 0, delay: 0 };
        render();
        stop = transports[name]();
    }

    root.addEventListener('change', event => {
        if (event.target.name === 'transport') start(event.target.value);
        // Another SignalR transport means a new connection.
        if (event.target.id === 'signalr-transport') {
            document.getElementById('transport-signalr').checked = true;
            start('signalr');
        }
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
