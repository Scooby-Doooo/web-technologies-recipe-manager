import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { createBrowserRouter, RouterProvider } from 'react-router';
import 'bootstrap/dist/css/bootstrap.min.css';
import './styles.css';
import Layout from './Layout.jsx';
import RecipeList from './pages/RecipeList.jsx';
import RecipeDetails from './pages/RecipeDetails.jsx';
import RecipeForm from './pages/RecipeForm.jsx';
import NotFound from './pages/NotFound.jsx';

const router = createBrowserRouter([
    {
        path: '/',
        element: <Layout />,
        children: [
            { index: true, element: <RecipeList /> },
            { path: 'recipes/new', element: <RecipeForm /> },
            { path: 'recipes/:id', element: <RecipeDetails /> },
            { path: 'recipes/:id/edit', element: <RecipeForm /> },
            { path: '*', element: <NotFound /> }
        ]
    }
], { basename: '/spa' });

createRoot(document.getElementById('root')).render(
    <StrictMode>
        <RouterProvider router={router} />
    </StrictMode>
);
