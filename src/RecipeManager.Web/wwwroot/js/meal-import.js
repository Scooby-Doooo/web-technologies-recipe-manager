// Import page: results update while the user types or changes a filter; a meal opens in a
// preview window with its ingredients and instructions before it is imported.
(() => {
    const form = document.getElementById('import-search');
    if (!form) return;
    const term = document.getElementById('import-term');
    const status = document.getElementById('import-status');
    const results = document.getElementById('import-results');
    const modal = document.getElementById('import-preview');
    const preview = {
        title: document.getElementById('import-preview-title'),
        error: document.getElementById('import-preview-error'),
        body: document.getElementById('import-preview-body'),
        image: document.getElementById('import-preview-image'),
        meta: document.getElementById('import-preview-meta'),
        ingredients: document.getElementById('import-preview-ingredients'),
        video: document.getElementById('import-preview-video'),
        instructions: document.getElementById('import-preview-instructions'),
        open: document.getElementById('import-preview-open'),
        form: document.getElementById('import-preview-form')
    };
    const importButton = preview.form.querySelector('button');
    const hint = status.textContent;
    let searchController = null;
    let previewController = null;
    let timer = null;

    function element(tag, className, text) {
        const node = document.createElement(tag);
        if (className) node.className = className;
        if (text !== undefined) node.textContent = text;
        return node;
    }

    // TheMealDB serves a small copy of every meal picture at "<picture>/preview".
    const small = url => url ? `${url}/preview` : null;

    function card(meal) {
        const column = element('div', 'col');
        const box = element('article', 'card h-100');
        if (meal.thumbnail) {
            const image = element('img', 'card-img-top');
            image.src = small(meal.thumbnail);
            image.alt = '';
            image.loading = 'lazy';
            box.append(image);
        }
        const body = element('div', 'card-body d-flex flex-column gap-2');
        body.append(element('h2', 'h6 card-title mb-0', meal.name));
        const button = element('button', 'btn btn-outline-primary btn-sm mt-auto', 'Preview');
        button.type = 'button';
        button.setAttribute('aria-label', `Preview ${meal.name}`);
        button.addEventListener('click', () => show(meal.id));
        if (meal.recipeId) body.append(element('span', 'badge text-bg-success align-self-start', 'In your kitchen'));
        body.append(button);
        box.append(body);
        column.append(box);
        return column;
    }

    async function search() {
        const params = new URLSearchParams(new FormData(form));
        for (const [key, value] of [...params]) if (!value.trim()) params.delete(key);
        history.replaceState(null, '', params.size ? `?${params}` : location.pathname);
        searchController?.abort();
        if ((params.get('term') || '').trim().length < 2 && !params.has('category') && !params.has('ingredient')) {
            results.replaceChildren();
            status.textContent = hint;
            return;
        }
        searchController = new AbortController();
        status.textContent = 'Searching…';
        try {
            const response = await fetch(`${form.action}?${params}`, { signal: searchController.signal, headers: { Accept: 'application/json' } });
            const data = await response.json();
            if (!response.ok) throw new Error(data.error || data.title || `The server answered ${response.status}.`);
            results.replaceChildren(...data.meals.map(card));
            status.textContent = data.total === 0 ? 'Nothing found. Try another name or filter.'
                : data.total > data.meals.length ? `Found ${data.total} meals, showing the first ${data.meals.length}.`
                : `Found ${data.total} ${data.total === 1 ? 'meal' : 'meals'}.`;
        } catch (error) {
            if (error.name === 'AbortError') return;
            results.replaceChildren();
            status.textContent = error.message;
        }
    }

    async function show(id) {
        previewController?.abort();
        previewController = new AbortController();
        preview.title.textContent = 'Loading…';
        preview.body.hidden = true;
        preview.error.hidden = true;
        preview.open.hidden = true;
        importButton.disabled = true;
        bootstrap.Modal.getOrCreateInstance(modal).show();
        try {
            const response = await fetch(`${form.dataset.mealUrl}/${id}`, { signal: previewController.signal });
            const meal = await response.json();
            if (!response.ok) throw new Error(meal.error || `The server answered ${response.status}.`);
            preview.title.textContent = meal.name;
            preview.image.hidden = !meal.thumbnail;
            if (meal.thumbnail) {
                preview.image.src = small(meal.thumbnail);
                preview.image.alt = `Photo of ${meal.name}`;
            }
            preview.meta.textContent = [meal.category, meal.area].filter(Boolean).join(' · ');
            preview.ingredients.replaceChildren(...meal.ingredients.map(item => {
                const line = element('li', 'd-flex justify-content-between gap-3');
                line.append(element('span', null, item.name), element('span', 'recipe-muted', item.measure));
                return line;
            }));
            const video = URL.canParse(meal.youtube || '') && new URL(meal.youtube).protocol === 'https:';
            preview.video.hidden = !video;
            if (video) preview.video.href = meal.youtube;
            preview.instructions.textContent = meal.instructions;
            preview.form.action = `${form.dataset.mealUrl}/${meal.id}`;
            if (meal.recipeId) {
                preview.open.href = `${form.dataset.recipeUrl}/${meal.recipeId}`;
                preview.open.hidden = false;
            }
            importButton.hidden = Boolean(meal.recipeId);
            importButton.disabled = false;
            preview.body.hidden = false;
        } catch (error) {
            if (error.name === 'AbortError') return;
            preview.title.textContent = 'Meal';
            preview.error.textContent = error.message;
            preview.error.hidden = false;
        }
    }

    form.addEventListener('submit', event => {
        event.preventDefault();
        clearTimeout(timer);
        search();
    });
    // Typing waits for a short pause; choosing a category or an ingredient searches at once.
    term.addEventListener('input', () => {
        clearTimeout(timer);
        timer = setTimeout(search, 400);
    });
    form.addEventListener('change', event => {
        if (event.target !== term) search();
    });
    preview.form.addEventListener('submit', () => { importButton.disabled = true; });

    // A shared or reloaded address keeps its search.
    const initial = new URLSearchParams(location.search);
    for (const [key, value] of initial) if (form.elements[key]) form.elements[key].value = value;
    if (initial.size) search();
})();
