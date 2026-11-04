import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

// Сборка кладётся в wwwroot веб-приложения — один процесс раздаёт и API, и интерфейс.
// В режиме разработки (npm run dev) запросы проксируются на scada-web (порт 8080).
// Режим demo (npm run build:demo): бэкенд заменён симулятором, один JS-файл → dist-demo/.
const backend = process.env.SCADA_BACKEND ?? 'http://localhost:8080';

export default defineConfig(({ mode }) => ({
  plugins: [react()],
  build: mode === 'demo'
    ? {
        outDir: 'dist-demo',
        emptyOutDir: true,
        chunkSizeWarningLimit: 3000,
        assetsInlineLimit: 100_000_000,
        rollupOptions: { output: { inlineDynamicImports: true } },
      }
    : {
        outDir: '../backend/src/Scada.Web/wwwroot',
        emptyOutDir: true,
        chunkSizeWarningLimit: 1500,
      },
  server: {
    port: 5173,
    proxy: {
      '/api': backend,
      '/health': backend,
      '/hubs': { target: backend, ws: true },
    },
  },
}));
