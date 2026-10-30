import { useEffect, useState } from 'react';
import { Link, useNavigate, useParams } from 'react-router';
import { api } from '../api.js';
import { useAccount } from '../Layout.jsx';
import NotFound from './NotFound.jsx';

export default function RecipeDetails() {
    const { id } = useParams();
    const account = useAccount();
    const navigate = useNavigate();
    const [recipe, setRecipe] = useState(null);
    const [error, setError] = useState(null);

    useEffect(() => {
        const abort = new AbortController();
        setRecipe(null);
        setError(null);
        api.recipe(id, abort.signal).then(setRecipe, failure => abort.signal.aborted || setError(failure));
        return () => abort.abort();
    }, [id]);

    const remove = async () => {
        if (!window.confirm(`Delete "${recipe.title}"?`)) return;
        try {
            await api.deleteRecipe(recipe.id);
            navigate('/', { replace: true });
        } catch (failure) {
            setError(failure);
        }
    };

    if (error?.status === 404) return <NotFound />;
    if (error) return <div className="alert alert-danger" role="alert">{error.message}</div>;
    if (!recipe) return <p role="status">Loading…</p>;

    return (
        <article className="row g-4">
            {recipe.imagePath && (
                <div className="col-md-5">
                    <img src={recipe.imagePath} className="img-fluid rounded" alt={`Photo of ${recipe.title}`} />
                </div>
            )}
            <div className={recipe.imagePath ? 'col-md-7' : 'col-12'}>
                <p className="text-muted mb-1">{recipe.categoryName}</p>
                <h1 className="h2">{recipe.title}</h1>
                <p className="mb-2">{recipe.cookingTimeMinutes} min · {recipe.servings} {recipe.servings === 1 ? 'serving' : 'servings'}</p>
                {recipe.description && <p className="lead">{recipe.description}</p>}
                {account?.signedIn && (
                    <div className="d-flex gap-2 mb-3">
                        <Link className="btn btn-primary" to={`/recipes/${recipe.id}/edit`}>Edit</Link>
                        <button type="button" className="btn btn-outline-danger" onClick={remove}>Delete</button>
                    </div>
                )}
                {recipe.ingredients.length > 0 && (
                    <>
                        <h2 className="h5">Ingredients</h2>
                        <ul>
                            {recipe.ingredients.map(item => (
                                <li key={item.ingredientId}>{item.quantity} {item.unit} {item.name}</li>
                            ))}
                        </ul>
                    </>
                )}
                <h2 className="h5">Instructions</h2>
                <p className="spa-instructions">{recipe.instructions}</p>
                <Link to="/">← All recipes</Link>
            </div>
        </article>
    );
}
