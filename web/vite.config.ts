import { defineConfig } from 'vite'
import vue from '@vitejs/plugin-vue'

export default defineConfig({
  plugins: [vue()],
  build: { outDir: 'dist' },
  // 开发模式：5173 同源代理到后端 serve（8890），WUI 不用填服务器地址
  server: {
    proxy: {
      '/health': 'http://127.0.0.1:8890',
      '/session': 'http://127.0.0.1:8890',
      '/projects': 'http://127.0.0.1:8890',
      '/backup': 'http://127.0.0.1:8890',
      '/confirm': 'http://127.0.0.1:8890',
      '/git': 'http://127.0.0.1:8890',
    },
  },
})
