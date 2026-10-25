import { defineConfig } from 'vitest/config';
import preact from '@preact/preset-vite';

// Production bundle: one IIFE + one stylesheet, injected into Assets/chat-template.html by
// ChatWebView.LoadHtmlTemplate ({{SPA_JS}} / {{SPA_CSS}}). The MSBuild target in the csproj embeds
// dist/spa.js and dist/spa.css into the VSIX assembly.
export default defineConfig({
  plugins: [preact()],
  build: {
    outDir: 'dist',
    emptyOutDir: true,
    target: 'chrome120', // WebView2 evergreen runtime
    cssCodeSplit: false,
    minify: true,
    sourcemap: false,
    lib: {
      entry: 'src/main.tsx',
      name: 'VsAcpSpa',
      formats: ['iife'],
      fileName: () => 'spa.js',
      cssFileName: 'spa',
    },
  },
  test: {
    environment: 'jsdom',
    include: ['tests/**/*.test.ts', 'tests/**/*.test.tsx'],
  },
});
