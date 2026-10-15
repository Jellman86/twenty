import { defineConfig } from 'vite';
import { svelte } from '@sveltejs/vite-plugin-svelte';
export default defineConfig({
  plugins: [svelte()],
  build: { outDir: '../src/Twenty.Api/wwwroot', emptyOutDir: true },
  server: {
    proxy: {
      '/api': 'http://127.0.0.1:8788',
      '/health': 'http://127.0.0.1:8788',
    },
  },
});
