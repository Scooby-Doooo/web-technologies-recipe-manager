import { createServer } from 'node:http';

// A stand-in for www.themealdb.com with three made-up meals, so tests never call the real site.
// It records every request, which lets tests check caching and which pictures were fetched.
// A 1 × 1 PNG; fixtures.js loads this module, so it does not import helpers.js.
const png = Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aOxoAAAAASUVORK5CYII=', 'base64');

export async function startFakeMealDb() {
    const requests = [];
    const state = { failing: false };
    const server = createServer((request, response) => {
        const url = new URL(request.url, 'http://fake');
        requests.push(url.pathname + url.search);
        const json = (status, body) => {
            response.writeHead(status, { 'Content-Type': 'application/json' });
            response.end(JSON.stringify(body));
        };
        if (url.pathname.startsWith('/images/')) {
            // The tart picture claims to be HTML, which the importer must refuse.
            const html = url.pathname.includes('qa-tart');
            response.writeHead(200, { 'Content-Type': html ? 'text/html' : 'image/png' });
            return response.end(html ? '<p>not a picture</p>' : png);
        }
        if (state.failing) return json(500, { error: 'down' });
        const method = url.pathname.replace('/api/json/v1/1/', '');
        const meals = fake.meals;
        const value = name => (url.searchParams.get(name) || '').toLowerCase();
        const summary = meal => ({ idMeal: meal.idMeal, strMeal: meal.strMeal, strMealThumb: meal.strMealThumb });
        const found = list => json(200, { meals: list.length ? list : null });
        if (method === 'list.php' && url.searchParams.has('c')) return found(['Seafood', 'Dessert', 'Chicken'].map(strCategory => ({ strCategory })));
        if (method === 'list.php' && url.searchParams.has('i')) return found(['Salmon', 'Garlic', 'Chicken', 'Flour', 'Milk'].map(strIngredient => ({ strIngredient })));
        if (method === 'search.php') return found(meals.filter(meal => meal.strMeal.toLowerCase().includes(value('s'))));
        if (method === 'lookup.php') return found(meals.filter(meal => meal.idMeal === url.searchParams.get('i')));
        if (method === 'filter.php' && url.searchParams.has('c')) return found(meals.filter(meal => meal.strCategory.toLowerCase() === value('c')).map(summary));
        if (method === 'filter.php' && url.searchParams.has('i')) {
            return found(meals.filter(meal => Object.entries(meal).some(([key, text]) =>
                key.startsWith('strIngredient') && text?.toLowerCase() === value('i'))).map(summary));
        }
        json(404, { error: 'unknown' });
    });
    await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
    const { port } = server.address();
    const origin = `http://127.0.0.1:${port}`;
    const ingredients = list => Object.fromEntries(list.flatMap(([name, measure], index) =>
        [[`strIngredient${index + 1}`, name], [`strMeasure${index + 1}`, measure]]));
    const fake = {
        meals: [
            {
                idMeal: '90001', strMeal: 'QA Garlic Salmon', strCategory: 'Seafood', strArea: 'British',
                strInstructions: 'Melt the butter.\r\nFry the salmon with garlic.\r\nServe with lemon.',
                strMealThumb: `${origin}/images/media/meals/qa-salmon.png`, strYoutube: 'https://www.youtube.com/watch?v=qa',
                ...ingredients([['Salmon', '2 fillets'], ['garlic', '3 cloves'], ['Butter', '1 1/2 tbsp'], ['Lemon', '½'],
                    ['Salt', 'To taste'], ['Dill', '2-3 sprigs'], ['Garlic', '2 cloves'], ['Milk', '200ml'], ['', ''], [null, null]])
            },
            {
                // The picture is on another host name, so the importer must not download it.
                idMeal: '90002', strMeal: 'QA Chicken Curry', strCategory: 'Chicken', strArea: 'Indian',
                strInstructions: 'Cook the chicken.', strMealThumb: `http://localhost:${port}/images/media/meals/qa-curry.png`,
                ...ingredients([['Chicken', '500g'], ['Garlic', '4 cloves']])
            },
            {
                idMeal: '90003', strMeal: 'QA Lemon Tart', strCategory: 'dessert', strArea: null, strCountry: 'France',
                strInstructions: 'Bake the tart.', strMealThumb: `${origin}/images/media/meals/qa-tart.png`,
                ...ingredients([['flour', '250g'], ['Milk', '¾ cup']])
            }
        ]
    };
    return {
        url: `${origin}/api/json/v1/1/`,
        requests,
        state,
        close: () => new Promise(resolve => server.close(resolve))
    };
}
