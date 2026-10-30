import { fileURLToPath } from 'node:url';
import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

// The web app serves the build under /spa, on the same origin as the API, so no CORS is needed.
// In dev the API calls are proxied to `dotnet run --launch-profile http`.
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
