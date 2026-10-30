import { useEffect, useState } from 'react';
import { Link, useNavigate, useParams } from 'react-router';
import { api, signInUrl } from '../api.js';
import { useAccount } from '../Layout.jsx';

const empty = { title: '', description: '', instructions: '', cookingTimeMinutes: '', servings: '', categoryId: '' };
const ImageTypes = ['image/jpeg', 'image/png', 'image/webp'];

// Used for both create and edit.
export default function RecipeForm() {
    const { id } = useParams();
    const account = useAccount();
    const navigate = useNavigate();
    const [form, setForm] = useState(empty);
    const [image, setImage] = useState(null);
    const [categories, setCategories] = useState([]);
    const [errors, setErrors] = useState({});
    const [message, setMessage] = useState(null);
    const [saving, setSaving] = useState(false);

    useEffect(() => {
        api.categories().then(setCategories, failure => setMessage(failure.message));
        if (id) api.recipe(id).then(recipe => setForm({
            title: recipe.title, description: recipe.description ?? '', instructions: recipe.instructions,
            cookingTimeMinutes: recipe.cookingTimeMinutes, servings: recipe.servings, categoryId: recipe.categoryId
        }), failure => setMessage(failure.message));
    }, [id]);

    if (account && !account.signedIn) {
        return <p>Please <a href={signInUrl()}>sign in</a> to {id ? 'edit' : 'add'} recipes.</p>;
    }

    const change = event => setForm(current => ({ ...current, [event.target.name]: event.target.value }));

    const submit = async event => {
        event.preventDefault();
        if (image && (!ImageTypes.includes(image.type) || image.size > 5 * 1024 * 1024)) {
            setErrors({ imageFile: ['Choose a JPG, PNG or WEBP image up to 5 MB.'] });
            return;
        }
        setSaving(true);
        setErrors({});
        setMessage(null);
        const recipe = {
            ...form,
            description: form.description || null,
            cookingTimeMinutes: Number(form.cookingTimeMinutes),
            servings: Number(form.servings),
            categoryId: Number(form.categoryId)
        };
        try {
            const saved = id ? await api.updateRecipe(id, recipe) : await api.createRecipe(recipe);
            if (image) await api.uploadImage(saved.id, image);
            navigate(`/recipes/${saved.id}`);
        } catch (failure) {
            // Field names come back as in the C# request: Title, CookingTimeMinutes…
            setErrors(Object.fromEntries(Object.entries(failure.errors).map(([key, value]) =>
                [key.charAt(0).toLowerCase() + key.slice(1), value])));
            setMessage(failure.message);
            setSaving(false);
        }
    };

    const field = (name, label, input) => (
        <div className="mb-3">
            <label className="form-label" htmlFor={`spa-${name}`}>{label}</label>
            {input({ id: `spa-${name}`, name, className: `form-control${errors[name] ? ' is-invalid' : ''}`,
                'aria-describedby': errors[name] ? `spa-${name}-error` : undefined })}
            {errors[name] && <div id={`spa-${name}-error`} className="invalid-feedback">{errors[name].join(' ')}</div>}
        </div>
    );

    return (
        <form noValidate onSubmit={submit} className="col-lg-8">
            <h1 className="h3 mb-3">{id ? 'Edit recipe' : 'New recipe'}</h1>
            {message && <div className="alert alert-danger" role="alert">{message}</div>}
            {field('title', 'Title', props => <input {...props} value={form.title} onChange={change} required maxLength={200} />)}
            {field('categoryId', 'Category', props => (
                <select {...props} className={props.className.replace('form-control', 'form-select')}
                    value={form.categoryId} onChange={change} required>
                    <option value="">Choose a category</option>
                    {categories.map(category => <option key={category.id} value={category.id}>{category.name}</option>)}
                </select>
            ))}
            {field('description', 'Description', props => <textarea {...props} rows={2} value={form.description} onChange={change} maxLength={2000} />)}
            <div className="row">
                <div className="col-sm-6">
                    {field('cookingTimeMinutes', 'Cooking time (minutes)', props =>
                        <input {...props} type="number" min={1} value={form.cookingTimeMinutes} onChange={change} required />)}
                </div>
                <div className="col-sm-6">
                    {field('servings', 'Servings', props =>
                        <input {...props} type="number" min={1} value={form.servings} onChange={change} required />)}
                </div>
            </div>
            {field('instructions', 'Instructions', props => <textarea {...props} rows={6} value={form.instructions} onChange={change} required maxLength={10000} />)}
            {field('imageFile', id ? 'New photo (optional)' : 'Photo (optional)', props =>
                <input {...props} type="file" accept=".jpg,.jpeg,.png,.webp" onChange={event => setImage(event.target.files[0] ?? null)} />)}
            <div className="d-flex gap-2">
                <button type="submit" className="btn btn-primary" disabled={saving}>{saving ? 'Saving…' : 'Save'}</button>
                <Link className="btn btn-outline-secondary" to={id ? `/recipes/${id}` : '/'}>Cancel</Link>
            </div>
        </form>
    );
}
