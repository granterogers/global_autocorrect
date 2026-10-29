// Bundles the ProseMirror and Lexical pages. `define` replaces process.env at build time and
// the "production" condition selects Lexical's production build, so pages never read process.env (12.5).
import { build } from 'esbuild';

await build({
  entryPoints: { prosemirror: 'src/prosemirror.js', lexical: 'src/lexical.js' },
  outdir: 'dist',
  bundle: true,
  minify: true,
  format: 'iife',
  conditions: ['production'],
  define: { 'process.env.NODE_ENV': '"production"' },
  logLevel: 'info',
});
