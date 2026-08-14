import { defineConfig } from 'vite'
import vue from '@vitejs/plugin-vue'

export default defineConfig({
  plugins: [vue()],
  base: './',
  server: {
    port: 5180,
    proxy: {
      '/api': 'http://127.0.0.1:58390',
    },
  },
})
