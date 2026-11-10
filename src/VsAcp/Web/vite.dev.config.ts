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

/**
 * The dev server's port: `VSACP_DEV_PORT`, else 5199. Windows can reserve blocks of ports for Hyper-V networking
 * (`netsh int ipv4 show excludedportrange protocol=tcp`), and binding a reserved port fails with EACCES; set the
 * variable to a free port then. The e2e harness and the screenshot scripts take the URL from the server they start.
 */
function devPort(): number {
  const value = process.env.VSACP_DEV_PORT;
  if (!value) return 5199;
  const port = Number(value);
  if (!Number.isInteger(port) || port < 1 || port > 65535) throw new Error(`VSACP_DEV_PORT is not a port number: ${value}`);
  return port;
}

// Dev harness / e2e server: serves dev/index.html with the mock host. Not part of the product build.
export default defineConfig({
  plugins: [preact(), vendorAssets()],
  server: { port: devPort(), strictPort: true, open: '/dev/index.html?sample' },
});
