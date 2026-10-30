import { fileURLToPath } from 'node:url';
import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

// The SPA is served by RecipeManager.Web under /spa, on the same origin as the JSON API, so the
// sign-in cookie and antiforgery token work without CORS. `npm run dev` proxies the API to the
// app started with `dotnet run --launch-profile http`.
const backend = 'http://localhost:5105';

export default defineConfig({
    plugins: [react()],
    base: '/spa/',
    build: {
        outDir: fileURLToPath(new URL('../RecipeManager.Web/wwwroot/spa', import.meta.url)),
        emptyOutDir: true
    },
    server: {
        port: 5173,
        proxy: { '/api': backend, '/Account': backend, '/uploads': backend }
    }
});
