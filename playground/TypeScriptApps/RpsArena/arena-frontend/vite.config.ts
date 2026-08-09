import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

export default defineConfig({
    plugins: [react()],
    server: {
        proxy: {
            '/api': {
                target: process.env.GAMEMASTER_HTTP || 'http://localhost:5100',
                changeOrigin: true,
            },
        },
    },
});
