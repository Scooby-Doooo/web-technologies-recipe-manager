// Notes editor (C12). The document is an implicit list of blocks, like in Notion: each block has
// an id, a type and plain Markdown text. The focused block shows its raw text; the others show
// the inline Markdown rendered. The whole document is kept in localStorage.
(() => {
    const root = document.getElementById('notes');
    if (!root) return;
    const view = document.getElementById('notes-document');
    const typeSelect = document.getElementById('notes-type');
    const status = document.getElementById('notes-status');
    const storageKey = `recipe-manager-notes-${root.dataset.recipeId}`;
    const labels = {
        paragraph: 'Text', heading1: 'Heading 1', heading2: 'Heading 2', heading3: 'Heading 3',
        bullet: 'Bulleted list', numbered: 'Numbered list', todo: 'To-do', quote: 'Quote'
    };
    // Markdown typed at the start of a text block turns it into another type.
    const shortcuts = [[/^###\s/, 'heading3'], [/^##\s/, 'heading2'], [/^#\s/, 'heading1'], [/^[-*]\s/, 'bullet'],
        [/^1[.)]\s/, 'numbered'], [/^\[ ?\]\s/, 'todo'], [/^>\s/, 'quote']];
    const lists = new Set(['bullet', 'numbered', 'todo']);
    let blocks = [];
    let focusedId = null;
    let saveTimer = null;

    const newId = () => crypto.randomUUID?.() ?? `${Date.now().toString(36)}-${Math.random().toString(36).slice(2)}`;
    const block = (type = 'paragraph', text = '', checked = false) => ({ id: newId(), type, text, checked });
    const find = id => blocks.findIndex(item => item.id === id);

    function load() {
        try {
            const saved = JSON.parse(localStorage.getItem(storageKey));
            if (saved?.version === 1 && Array.isArray(saved.blocks)) {
                // Stored data is checked like any input: unknown types or fields are dropped.
                const valid = saved.blocks
                    .filter(item => item && typeof item.id === 'string' && labels[item.type] && typeof item.text === 'string')
                    .map(item => ({ id: item.id, type: item.type, text: item.text, checked: item.checked === true }));
                if (valid.length) {
                    status.textContent = 'Restored from this browser';
                    return valid;
                }
            }
        } catch {
            // Storage is off or holds something broken: start a new document.
        }
        return [block('heading1', `Notes for ${root.dataset.title}`), block()];
    }

    function save() {
        clearTimeout(saveTimer);
        saveTimer = null;
        try {
            localStorage.setItem(storageKey, JSON.stringify({ version: 1, savedAt: Date.now(), blocks }));
            status.textContent = `Saved in this browser at ${new Date().toLocaleTimeString()}`;
        } catch {
            status.textContent = 'This browser does not allow saving notes.';
        }
    }

    function saveSoon() {
        clearTimeout(saveTimer);
        saveTimer = setTimeout(save, 300);
        status.textContent = 'Saving…';
    }

    const escape = text => text.replace(/[&<>"']/g, char => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[char]);

    // Text is escaped first, so the only HTML is the tags added here. Only http(s) links become
    // <a>, which keeps "javascript:" addresses as plain text.
    function inline(text) {
        return text.split(/(`[^`\n]+`)/).map(part => /^`[^`\n]+`$/.test(part)
            ? `<code>${escape(part.slice(1, -1))}</code>`
            : escape(part)
                .replace(/\[([^\]\n]+)\]\((https?:\/\/[^\s)]+)\)/g, '<a href="$2" target="_blank" rel="noopener noreferrer">$1</a>')
                .replace(/\*\*([^*\n]+)\*\*/g, '<strong>$1</strong>')
                .replace(/\*([^*\n]+)\*/g, '<em>$1</em>')).join('');
    }

    function number(index) {
        let value = 1;
        for (let i = index - 1; i >= 0 && blocks[i].type === 'numbered'; i--) value++;
        return value;
    }

    function marker(text) {
        const node = document.createElement('span');
        node.className = 'notes-marker';
        node.setAttribute('aria-hidden', 'true');
        node.textContent = text;
        return node;
    }

    // An editable element does not show an empty last line, so text ending with a line break
    // gets one more while it is edited; read() takes it off again. Browsers may also type a
    // no-break space into an editable element; the document keeps plain spaces.
    const shown = text => text.endsWith('\n') ? `${text}\n` : text;
    const read = element => {
        const text = element.textContent.replace(/ /g, ' ');
        return text.endsWith('\n') ? text.slice(0, -1) : text;
    };

    function show(text, item, editing) {
        if (editing) {
            if (text.textContent !== shown(item.text)) text.textContent = shown(item.text);
        } else {
            text.innerHTML = inline(item.text);
        }
        text.classList.toggle('notes-empty', !item.text);
    }

    function row(item, index) {
        const element = document.createElement('div');
        element.className = `notes-block notes-${item.type}`;
        element.classList.toggle('notes-done', item.type === 'todo' && item.checked);
        element.dataset.id = item.id;
        const handle = document.createElement('button');
        handle.type = 'button';
        handle.className = 'notes-handle';
        handle.draggable = true;
        handle.textContent = '⠿';
        handle.title = 'Drag to move, or press Arrow Up / Arrow Down';
        handle.setAttribute('aria-label', 'Drag to reorder');
        element.append(handle);
        if (item.type === 'bullet') element.append(marker('•'));
        if (item.type === 'numbered') element.append(marker(`${number(index)}.`));
        if (item.type === 'todo') {
            const box = document.createElement('input');
            box.type = 'checkbox';
            box.className = 'form-check-input notes-check';
            box.checked = item.checked;
            box.setAttribute('aria-label', 'Done');
            box.addEventListener('change', () => {
                item.checked = box.checked;
                element.classList.toggle('notes-done', box.checked);
                saveSoon();
            });
            element.append(box);
        }
        const text = document.createElement('div');
        text.className = 'notes-text';
        text.contentEditable = 'true';
        text.setAttribute('role', 'textbox');
        text.setAttribute('aria-multiline', 'true');
        text.setAttribute('aria-label', labels[item.type]);
        text.dataset.placeholder = item.type === 'paragraph' ? 'Type text, or # and a space for a heading' : labels[item.type];
        // A re-rendered block starts rendered; focusing it shows the raw text.
        show(text, item, false);
        element.append(text);
        return element;
    }

    function render(focus) {
        view.replaceChildren(...blocks.map(row));
        if (focus) place(focus.id, focus.offset);
    }

    function caretOffset(text) {
        const selection = getSelection();
        if (!selection.rangeCount || !text.contains(selection.anchorNode)) return 0;
        const range = selection.getRangeAt(0);
        const before = document.createRange();
        before.selectNodeContents(text);
        before.setEnd(range.startContainer, range.startOffset);
        return before.toString().length;
    }

    function place(id, offset) {
        const text = view.querySelector(`[data-id="${CSS.escape(id)}"] .notes-text`);
        if (!text) return;
        text.focus();
        const range = document.createRange();
        const walker = document.createTreeWalker(text, NodeFilter.SHOW_TEXT);
        let left = offset;
        let node;
        range.selectNodeContents(text);
        range.collapse(false);
        while ((node = walker.nextNode())) {
            if (left <= node.length) {
                range.setStart(node, left);
                range.collapse(true);
                break;
            }
            left -= node.length;
        }
        const selection = getSelection();
        selection.removeAllRanges();
        selection.addRange(range);
    }

    function setType(item, type) {
        item.type = type;
        if (type !== 'todo') item.checked = false;
    }

    // One line of Markdown, as pasted or exported, back to a block.
    function fromLine(line) {
        const todo = line.match(/^[-*]\s+\[([ xX])\]\s+(.*)$/);
        if (todo) return block('todo', todo[2], todo[1] !== ' ');
        for (const [pattern, type] of [[/^###\s+/, 'heading3'], [/^##\s+/, 'heading2'], [/^#\s+/, 'heading1'],
            [/^[-*]\s+/, 'bullet'], [/^\d+[.)]\s+/, 'numbered'], [/^>\s?/, 'quote']]) {
            if (pattern.test(line)) return block(type, line.replace(pattern, ''));
        }
        return block('paragraph', line);
    }

    function toMarkdown() {
        let counter = 0;
        return blocks.map((item, index) => {
            counter = item.type === 'numbered' ? counter + 1 : 0;
            // Two spaces before a line break keep it inside the same Markdown paragraph.
            const text = item.text.replace(/\n/g, '  \n');
            const prefix = {
                heading1: '# ', heading2: '## ', heading3: '### ', bullet: '- ', numbered: `${counter}. `,
                todo: `- [${item.checked ? 'x' : ' '}] `, quote: '> ', paragraph: ''
            }[item.type];
            const line = prefix + (item.type === 'quote' ? text.replace(/\n/g, '\n> ') : text);
            // Items of one list stay together; everything else is separated by an empty line.
            const previous = blocks[index - 1];
            return index === 0 ? line : (previous.type === item.type && lists.has(item.type) ? '\n' : '\n\n') + line;
        }).join('') + '\n';
    }

    function blockOf(target) {
        const text = target.closest?.('.notes-text');
        const element = text?.closest('.notes-block');
        const index = element ? find(element.dataset.id) : -1;
        return index < 0 ? null : { text, index, item: blocks[index] };
    }

    view.addEventListener('focusin', event => {
        const current = blockOf(event.target);
        if (!current) return;
        focusedId = current.item.id;
        typeSelect.value = current.item.type;
        // Rendered Markdown differs from the raw text; then the caret goes to the end.
        const rendered = current.text.textContent !== shown(current.item.text);
        show(current.text, current.item, true);
        if (rendered) place(current.item.id, current.item.text.length);
    });

    view.addEventListener('focusout', event => {
        const current = blockOf(event.target);
        if (current) show(current.text, current.item, false);
    });

    view.addEventListener('input', event => {
        const current = blockOf(event.target);
        if (!current) return;
        const { item, text } = current;
        item.text = read(text);
        text.classList.toggle('notes-empty', !item.text);
        if (item.type === 'paragraph') {
            const shortcut = shortcuts.find(([pattern]) => pattern.test(item.text));
            if (shortcut) {
                const prefix = item.text.match(shortcut[0])[0].length;
                const offset = caretOffset(text) - prefix;
                item.text = item.text.slice(prefix);
                setType(item, shortcut[1]);
                typeSelect.value = item.type;
                render({ id: item.id, offset: Math.max(0, offset) });
            }
        }
        saveSoon();
    });

    view.addEventListener('keydown', event => {
        const current = blockOf(event.target);
        if (!current || event.isComposing) return;
        const { item, index, text } = current;
        const offset = caretOffset(text);
        const collapsed = getSelection().isCollapsed;
        if (event.key === 'Enter' && event.shiftKey) {
            // A line break inside the same block. insertText would add a <br>, which is not part of the text.
            event.preventDefault();
            item.text = `${item.text.slice(0, offset)}\n${item.text.slice(offset)}`;
            show(text, item, true);
            place(item.id, offset + 1);
            saveSoon();
        } else if (event.key === 'Enter') {
            event.preventDefault();
            if (lists.has(item.type) && !item.text) {
                // Enter on an empty list item ends the list, like in Notion.
                setType(item, 'paragraph');
                render({ id: item.id, offset: 0 });
            } else {
                const next = block(lists.has(item.type) ? item.type : 'paragraph', item.text.slice(offset));
                item.text = item.text.slice(0, offset);
                blocks.splice(index + 1, 0, next);
                render({ id: next.id, offset: 0 });
            }
            saveSoon();
        } else if (event.key === 'Backspace' && collapsed && offset === 0) {
            if (item.type !== 'paragraph') {
                event.preventDefault();
                setType(item, 'paragraph');
                render({ id: item.id, offset: 0 });
                saveSoon();
            } else if (index > 0) {
                // Joins this block to the end of the previous one.
                event.preventDefault();
                const previous = blocks[index - 1];
                const joint = previous.text.length;
                previous.text += item.text;
                blocks.splice(index, 1);
                render({ id: previous.id, offset: joint });
                saveSoon();
            }
        } else if (event.key === 'Delete' && collapsed && offset === item.text.length && index < blocks.length - 1) {
            event.preventDefault();
            item.text += blocks[index + 1].text;
            blocks.splice(index + 1, 1);
            render({ id: item.id, offset });
            saveSoon();
        } else if (event.key === 'ArrowUp' && collapsed && offset === 0 && index > 0) {
            event.preventDefault();
            place(blocks[index - 1].id, blocks[index - 1].text.length);
        } else if (event.key === 'ArrowDown' && collapsed && offset === item.text.length && index < blocks.length - 1) {
            event.preventDefault();
            place(blocks[index + 1].id, 0);
        }
    });

    // Pasted text stays plain; several lines become blocks, read as Markdown.
    view.addEventListener('paste', event => {
        const current = blockOf(event.target);
        if (!current) return;
        event.preventDefault();
        const pasted = event.clipboardData.getData('text/plain').replace(/\r\n?/g, '\n');
        if (!pasted.includes('\n')) {
            document.execCommand('insertText', false, pasted);
            return;
        }
        const { item, index, text } = current;
        const offset = caretOffset(text);
        const rest = item.text.slice(offset);
        item.text = item.text.slice(0, offset);
        const added = pasted.split('\n').filter(line => line.trim()).map(fromLine);
        if (!added.length) return;
        const last = added.at(-1);
        const end = last.text.length;
        last.text += rest;
        // An empty text block is replaced by what was pasted instead of staying above it.
        const replace = item.type === 'paragraph' && !item.text;
        blocks.splice(replace ? index : index + 1, replace ? 1 : 0, ...added);
        render({ id: last.id, offset: end });
        saveSoon();
    });

    // Sorting (F3). Blocks that change place slide there: their old position is measured first,
    // then each one is animated from it to the new position (the FLIP technique).
    const reduceMotion = matchMedia('(prefers-reduced-motion: reduce)');

    function animated(change) {
        const before = new Map([...view.children].map(element => [element.dataset.id, element.getBoundingClientRect().top]));
        change();
        if (reduceMotion.matches) return;
        for (const element of view.children) {
            const shift = before.get(element.dataset.id) - element.getBoundingClientRect().top;
            if (shift) element.animate([{ transform: `translateY(${shift}px)` }, { transform: 'none' }], { duration: 200, easing: 'ease-out' });
        }
    }

    // While a block is dragged, the DOM is reordered under the pointer; the list itself takes
    // the new order only on drop, so a cancelled drag puts everything back.
    let dragged = null;
    let dropped = false;

    view.addEventListener('dragstart', event => {
        if (!event.target.classList?.contains('notes-handle')) return;
        dragged = event.target.closest('.notes-block');
        dropped = false;
        event.dataTransfer.effectAllowed = 'move';
        event.dataTransfer.setData('text/plain', ''); // Firefox starts a drag only with data set
        event.dataTransfer.setDragImage(dragged, 0, 0);
        dragged.classList.add('notes-dragging');
    });

    view.addEventListener('dragover', event => {
        if (!dragged) return;
        event.preventDefault();
        event.dataTransfer.dropEffect = 'move';
        const over = event.target.closest?.('.notes-block');
        if (!over || over === dragged) return;
        const box = over.getBoundingClientRect();
        const after = event.clientY > box.top + box.height / 2;
        if ((after ? over.nextElementSibling : over.previousElementSibling) === dragged) return;
        animated(() => view.insertBefore(dragged, after ? over.nextElementSibling : over));
    });

    view.addEventListener('drop', event => {
        if (!dragged) return;
        event.preventDefault();
        dropped = true;
    });

    view.addEventListener('dragend', () => {
        if (!dragged) return;
        if (dropped) {
            const order = [...view.children].map(element => element.dataset.id);
            blocks.sort((a, b) => order.indexOf(a.id) - order.indexOf(b.id));
            save();
        }
        dragged = null;
        // Rendering again renumbers the lists, or restores the old order after a cancelled drag.
        animated(() => render());
    });

    // The handle also works from the keyboard, for people who do not use a mouse.
    view.addEventListener('keydown', event => {
        if (!event.target.classList?.contains('notes-handle') || !['ArrowUp', 'ArrowDown'].includes(event.key)) return;
        event.preventDefault();
        const index = find(event.target.closest('.notes-block').dataset.id);
        const target = index + (event.key === 'ArrowUp' ? -1 : 1);
        if (target < 0 || target >= blocks.length) return;
        const [item] = blocks.splice(index, 1);
        blocks.splice(target, 0, item);
        animated(() => render());
        view.querySelector(`[data-id="${CSS.escape(item.id)}"] .notes-handle`).focus();
        save();
        status.textContent = `Moved to position ${target + 1} of ${blocks.length}`;
    });

    // Links in an editable block do not open on a plain click; Ctrl or Cmd + click opens them.
    view.addEventListener('click', event => {
        const link = event.target.closest('a');
        if (link && (event.ctrlKey || event.metaKey)) {
            event.preventDefault();
            window.open(link.href, '_blank', 'noopener');
        }
    });

    typeSelect.addEventListener('change', () => {
        const index = Math.max(0, find(focusedId));
        const item = blocks[index];
        setType(item, typeSelect.value);
        render({ id: item.id, offset: item.text.length });
        save();
    });

    document.getElementById('notes-download').addEventListener('click', () => {
        const link = document.createElement('a');
        link.href = URL.createObjectURL(new Blob([toMarkdown()], { type: 'text/markdown' }));
        link.download = `recipe-${root.dataset.recipeId}-notes.md`;
        link.click();
        setTimeout(() => URL.revokeObjectURL(link.href), 1000);
    });

    document.getElementById('notes-clear').addEventListener('click', () => {
        if (!confirm('Delete all notes for this recipe?')) return;
        blocks = [block()];
        render({ id: blocks[0].id, offset: 0 });
        save();
    });

    // Closing the tab must not lose the last 300 ms of typing.
    window.addEventListener('pagehide', () => { if (saveTimer) save(); });

    blocks = load();
    render();
})();
