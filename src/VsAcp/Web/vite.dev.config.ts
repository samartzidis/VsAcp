import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { defineConfig, type Plugin } from 'vite';
import preact from '@preact/preset-vite';

const assets = fileURLToPath(new URL('../Assets/', import.meta.url));

/** Serves the page's real vendor scripts (Showdown, highlight.js + theme) from Assets/ as /vendor/*. */
function vendorAssets(): Plugin {
  return {
    name: 'vendor-assets',
    configureServer(server) {
      server.middlewares.use('/vendor', (req, res, next) => {
        const name = (req.url ?? '').replace(/^\//, '').split('?')[0] ?? '';
        if (!/^[\w.-]+$/.test(name)) return next();
        try {
          const body = readFileSync(assets + name);
          res.setHeader('content-type', name.endsWith('.css') ? 'text/css' : 'text/javascript');
          res.end(body);
        } catch {
          next();
        }
      });
    },
  };
}

// Dev harness / e2e server: serves dev/index.html with the mock host. Not part of the product build.
export default defineConfig({
  plugins: [preact(), vendorAssets()],
  server: { port: 5199, strictPort: true, open: '/dev/index.html?sample' },
});
