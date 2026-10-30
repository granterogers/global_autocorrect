// Bundles the ProseMirror, Lexical, CKEditor 5, Quill, TinyMCE and Slate pages (stylesheets go to dist/<page>.css).
// `define` replaces process.env at build time and the "production" condition selects Lexical's production build, so
// pages never read process.env (12.5). TinyMCE is self-hosted from npm: its skin and content CSS are bundled in as
// resources, so nothing is fetched from Tiny Cloud.
import { build } from 'esbuild';

await build({
  entryPoints: {
    prosemirror: 'src/prosemirror.js',
    lexical: 'src/lexical.js',
    ckeditor: 'src/ckeditor.js',
    quill: 'src/quill.js',
    tinymce: 'src/tinymce.js',
    slate: 'src/slate.jsx',
  },
  outdir: 'dist',
  bundle: true,
  minify: true,
  format: 'iife',
  jsx: 'automatic',
  conditions: ['production'],
  define: { 'process.env.NODE_ENV': '"production"' },
  logLevel: 'info',
});
