import { Link } from 'react-router';

export default function NotFound() {
    return (
        <>
            <h1 className="h3">Not found</h1>
            <p>There is no such recipe or page.</p>
            <Link to="/">← All recipes</Link>
        </>
    );
}
