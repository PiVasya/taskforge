import { fileURLToPath, URL } from 'node:url'   // ⬅️ для вычисления абсолютного пути
import { defineConfig } from 'vite'
import vue from '@vitejs/plugin-vue'            // можно сразу ‘vue’, а не generic “plugin”

export default defineConfig({
  plugins: [vue()],
  resolve: {
    alias: {
      // "@/…" → "src/…"
      '@': fileURLToPath(new URL('./src', import.meta.url))
    }
  },
  server: {
    port: 80          // ▸ dev-сервер слушает 80-й порт
  }
})
