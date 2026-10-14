// Chat page of the AI assistant. The server answers with Server-Sent Events; fetch is used
// instead of EventSource because the request is a POST with the conversation in its body.
(() => {
    const chat = document.getElementById('assistant-chat');
    const form = document.getElementById('assistant-form');
    const input = document.getElementById('assistant-input');
    const send = document.getElementById('assistant-send');
    const stop = document.getElementById('assistant-stop');
    const reset = document.getElementById('assistant-reset');
    const errorBox = document.getElementById('assistant-error');
    const token = form.querySelector('input[name="__RequestVerificationToken"]').value;
    const storageKey = 'recipe-manager-assistant';
    let history = [];
    let controller = null;

    try { history = JSON.parse(sessionStorage.getItem(storageKey)) || []; } catch { history = []; }
    const save = () => { try { sessionStorage.setItem(storageKey, JSON.stringify(history.slice(-20))); } catch { /* storage is off */ } };

    // All text goes through textContent, so nothing from the model or the user becomes HTML.
    function element(tag, className, text) {
        const node = document.createElement(tag);
        if (className) node.className = className;
        if (text !== undefined) node.textContent = text;
        return node;
    }

    function addUser(text) {
        chat.append(element('div', 'assistant-message user', text));
    }

    function addAssistant(text = '') {
        const box = element('div', 'assistant-message assistant');
        const answer = element('div', 'assistant-answer', text);
        box.append(answer);
        chat.append(box);
        return { box, answer, thinking: null, tools: new Map() };
    }

    function pretty(json) {
        try { return JSON.stringify(JSON.parse(json), null, 2); } catch { return json; }
    }

    function handle(turn, type, data) {
        if (type === 'reasoning') {
            if (!turn.thinking) {
                const details = element('details', 'assistant-thinking');
                details.append(element('summary', null, 'Thinking'), element('pre'));
                turn.box.insertBefore(details, turn.answer);
                turn.thinking = details.querySelector('pre');
            }
            turn.thinking.textContent += data.text;
        } else if (type === 'tool_call') {
            const card = element('div', 'assistant-tool');
            card.append(element('div', null, `Tool: ${data.name}`), element('pre', 'assistant-tool-arguments', pretty(data.arguments || '{}')));
            turn.box.insertBefore(card, turn.answer);
            turn.tools.set(data.id, card);
        } else if (type === 'tool_result') {
            const result = element('details');
            result.append(element('summary', null, 'Result'), element('pre', 'assistant-tool-result', pretty(data.result)));
            turn.tools.get(data.id)?.append(result);
        } else if (type === 'content') {
            // Models often start an answer after tool calls with blank lines.
            turn.answer.textContent += turn.answer.textContent ? data.text : data.text.trimStart();
        } else if (type === 'error') {
            showError(data.message);
        }
    }

    function showError(message) {
        errorBox.textContent = message;
        errorBox.hidden = false;
    }

    // An SSE event ends with an empty line and has "event:" and "data:" lines.
    function parseEvent(raw) {
        const type = raw.match(/^event: (.+)$/m)?.[1];
        const data = raw.match(/^data: (.+)$/m)?.[1];
        return type && data ? { type, data: JSON.parse(data) } : null;
    }

    async function ask(text) {
        errorBox.hidden = true;
        history.push({ role: 'user', content: text });
        addUser(text);
        const turn = addAssistant();
        controller = new AbortController();
        send.disabled = true;
        stop.hidden = false;
        try {
            const response = await fetch(form.action, {
                method: 'POST',
                headers: { 'Content-Type': 'application/json', 'RequestVerificationToken': token },
                body: JSON.stringify({ messages: history.slice(-20) }),
                signal: controller.signal
            });
            if (!response.ok || !response.body) {
                const problem = await response.json().catch(() => null);
                throw new Error(problem?.error || `The server answered ${response.status}.`);
            }
            const reader = response.body.getReader();
            const decoder = new TextDecoder();
            let buffer = '';
            for (;;) {
                const { value, done } = await reader.read();
                if (done) break;
                buffer += decoder.decode(value, { stream: true });
                let end;
                while ((end = buffer.indexOf('\n\n')) >= 0) {
                    const event = parseEvent(buffer.slice(0, end));
                    buffer = buffer.slice(end + 2);
                    if (event) handle(turn, event.type, event.data);
                }
            }
        } catch (error) {
            if (error.name !== 'AbortError') showError(error.message);
        } finally {
            if (turn.answer.textContent) history.push({ role: 'assistant', content: turn.answer.textContent });
            else history.pop(); // nothing came back, so the question can be asked again
            save();
            controller = null;
            send.disabled = false;
            stop.hidden = true;
            input.focus();
        }
    }

    form.addEventListener('submit', event => {
        event.preventDefault();
        const text = input.value.trim();
        if (!text || controller) return;
        input.value = '';
        ask(text);
    });
    input.addEventListener('keydown', event => {
        if (event.key === 'Enter' && !event.shiftKey) {
            event.preventDefault();
            form.requestSubmit();
        }
    });
    stop.addEventListener('click', () => controller?.abort());
    reset.addEventListener('click', () => {
        controller?.abort();
        history = [];
        save();
        chat.replaceChildren();
        errorBox.hidden = true;
    });

    for (const message of history) {
        if (message.role === 'user') addUser(message.content); else addAssistant(message.content);
    }
})();
