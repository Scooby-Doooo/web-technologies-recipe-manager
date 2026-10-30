export class ApiError extends Error {
    constructor(status, problem) {
        super(problem?.title ?? `The server answered ${status}.`);
        this.status = status;
        // ValidationProblemDetails: { errors: { Title: ["…"] } }
        this.errors = problem?.errors ?? {};
    }
}

// The antiforgery token belongs to the signed-in user, so it is fetched once per page load.
let antiforgery;
function token() {
    antiforgery ??= fetch('/api/antiforgery').then(response => {
        if (!response.ok) throw new ApiError(response.status);
        return response.json();
    });
    return antiforgery;
}

async function request(method, path, body, signal) {
    const headers = { Accept: 'application/json' };
    if (method !== 'GET') {
        const { headerName, requestToken } = await token();
        headers[headerName] = requestToken;
    }
    if (body && !(body instanceof FormData)) {
        headers['Content-Type'] = 'application/json';
        body = JSON.stringify(body);
    }
    const response = await fetch(path, { method, headers, body, signal });
    const data = response.headers.get('content-type')?.includes('json') ? await response.json() : null;
    if (!response.ok) throw new ApiError(response.status, data);
    return data;
}

const query = params => new URLSearchParams(Object.entries(params).filter(([, value]) => value !== '' && value != null));

export const api = {
    account: () => request('GET', '/api/account'),
    categories: () => request('GET', '/api/categories'),
    recipes: (skip, limit, signal) => request('GET', `/api/recipes?${query({ skip, limit })}`, null, signal),
    search: (q, maxMinutes, skip, limit, signal) =>
        request('GET', `/api/recipes/search?${query({ q, maxMinutes, skip, limit })}`, null, signal),
    page: (link, signal) => request('GET', link, null, signal),
    recipe: (id, signal) => request('GET', `/api/recipes/${id}`, null, signal),
    createRecipe: recipe => request('POST', '/api/recipes', recipe),
    updateRecipe: (id, recipe) => request('PUT', `/api/recipes/${id}`, recipe),
    deleteRecipe: id => request('DELETE', `/api/recipes/${id}`),
    uploadImage: (id, file) => {
        const form = new FormData();
        form.append('imageFile', file);
        return request('POST', `/api/recipes/${id}/image`, form);
    }
};

export const signInUrl = () => '/Account/Login?returnUrl=' + encodeURIComponent(location.pathname + location.search);
