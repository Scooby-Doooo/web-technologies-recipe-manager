import { useEffect, useState } from 'react';
import { Link, NavLink, Outlet, useOutletContext } from 'react-router';
import { api, signInUrl } from './api.js';

// The account is loaded once and shared with the pages through the outlet context.
export default function Layout() {
    const [account, setAccount] = useState(null);

    useEffect(() => {
        api.account().then(setAccount, () => setAccount({ signedIn: false }));
    }, []);

    return (
        <>
            <header className="navbar navbar-expand bg-body-tertiary border-bottom mb-4">
                <nav className="container" aria-label="Main">
                    <Link className="navbar-brand" to="/">Recipe Manager <span className="badge text-bg-success">SPA</span></Link>
                    <ul className="navbar-nav me-auto">
                        <li className="nav-item"><NavLink className="nav-link" to="/" end>Recipes</NavLink></li>
                        {account?.signedIn && <li className="nav-item"><NavLink className="nav-link" to="/recipes/new">New recipe</NavLink></li>}
                    </ul>
                    <div className="navbar-text small" aria-live="polite">
                        {account == null ? 'Loading…'
                            : account.signedIn ? <span id="spa-account">{account.name} · {account.kitchen}</span>
                            : <a href={signInUrl()}>Sign in</a>}
                        <a className="ms-3" href="/">Classic site</a>
                    </div>
                </nav>
            </header>
            <main className="container pb-5">
                <Outlet context={account} />
            </main>
        </>
    );
}

export const useAccount = () => useOutletContext();
