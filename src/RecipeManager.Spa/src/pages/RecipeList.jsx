import { useEffect, useState } from 'react';
import { Link, useSearchParams } from 'react-router';
import { api } from '../api.js';

const PageSize = 12;

// Without a query shows the API pages, with one uses full-text search. The query is kept in the URL.
export default function RecipeList() {
    const [params, setParams] = useSearchParams();
    const q = params.get('q') ?? '';
    const maxMinutes = params.get('maxMinutes') ?? '';
    const [text, setText] = useState(q);
    const [result, setResult] = useState({ items: [], total: null, nextLink: null });
    const [state, setState] = useState('loading');
    const [error, setError] = useState(null);

    // Back and Forward change the address; the box follows it.
    useEffect(() => setText(current => current.trim() === q ? current : q), [q]);

    // Typing updates the address 300 ms after the last key.
    useEffect(() => {
        if (text.trim() === q) return;
        const timer = setTimeout(() => setParams(current => {
            const next = new URLSearchParams(current);
            if (text.trim()) next.set('q', text.trim()); else next.delete('q');
            return next;
        }, { replace: true }), 300);
        return () => clearTimeout(timer);
    }, [text, q, setParams]);

    useEffect(() => {
        // A slower answer for an older query must not replace a newer one.
        const abort = new AbortController();
        setState('loading');
        setError(null);
        const load = q
            ? api.search(q, maxMinutes, 0, PageSize, abort.signal)
            : api.recipes(0, PageSize, abort.signal).then(page => ({ ...page, total: null }));
        load.then(page => { setResult(page); setState('done'); }, failure => {
            if (abort.signal.aborted) return;
            setError(failure.status === 400 ? Object.values(failure.errors).flat()[0] ?? failure.message : failure.message);
            setResult({ items: [], total: null, nextLink: null });
            setState('failed');
        });
        return () => abort.abort();
    }, [q, maxMinutes]);

    const loadMore = async () => {
        setState('loading');
        try {
            const page = await api.page(result.nextLink);
            setResult(current => ({ ...page, total: current.total, items: [...current.items, ...page.items] }));
            setState('done');
        } catch (failure) {
            setError(failure.message);
            setState('failed');
        }
    };

    const setMaxMinutes = value => setParams(current => {
        const next = new URLSearchParams(current);
        if (value) next.set('maxMinutes', value); else next.delete('maxMinutes');
        return next;
    });

    return (
        <>
            <h1 className="h3 mb-3">Recipes</h1>
            <form className="row g-2 mb-3" role="search" onSubmit={event => event.preventDefault()}>
                <div className="col-sm-8">
                    <label className="visually-hidden" htmlFor="spa-search">Search recipes</label>
                    <input id="spa-search" type="search" className="form-control" placeholder="Search: garlic, title:soup, pan*…"
                        value={text} maxLength={200} onChange={event => setText(event.target.value)} />
                </div>
                <div className="col-sm-4">
                    <label className="visually-hidden" htmlFor="spa-time">Cooking time</label>
                    <select id="spa-time" className="form-select" value={maxMinutes} disabled={!q}
                        onChange={event => setMaxMinutes(event.target.value)}>
                        <option value="">Any time</option>
                        <option value="15">Up to 15 min</option>
                        <option value="30">Up to 30 min</option>
                        <option value="60">Up to 1 hour</option>
                    </select>
                </div>
            </form>

            <p className="text-muted small" role="status">
                {state === 'loading' ? 'Loading…'
                    : error ? null
                    : q ? `${result.total} ${result.total === 1 ? 'recipe' : 'recipes'} found`
                    : `${result.items.length} recipes shown`}
            </p>
            {error && <div className="alert alert-danger" role="alert">{error}</div>}

            <div className="row row-cols-1 row-cols-sm-2 row-cols-lg-3 g-3">
                {result.items.map(recipe => (
                    <div className="col" key={recipe.id}>
                        <article className="card h-100 spa-card">
                            {recipe.imagePath && <img src={recipe.imagePath} className="card-img-top" alt="" loading="lazy" />}
                            <div className="card-body">
                                <h2 className="h5 card-title">
                                    <Link className="stretched-link text-decoration-none" to={`/recipes/${recipe.id}`}>{recipe.title}</Link>
                                </h2>
                                <p className="card-text text-muted small mb-0">
                                    {recipe.categoryName} · {recipe.cookingTimeMinutes} min
                                </p>
                            </div>
                        </article>
                    </div>
                ))}
            </div>
            {state === 'done' && result.items.length === 0 && <p>No recipes{q ? ' match this search' : ' yet'}.</p>}
            {result.nextLink && (
                <button type="button" className="btn btn-outline-primary mt-3" disabled={state === 'loading'} onClick={loadMore}>
                    Load more
                </button>
            )}
        </>
    );
}
