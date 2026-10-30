// Serves artifacts/progress.html on 127.0.0.1 only, so the live progress page can refresh itself in a browser.
import { createServer } from 'node:http';
import { readFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';

const page = join(dirname(fileURLToPath(import.meta.url)), '..', 'artifacts', 'progress.html');
const port = Number(process.env.PORT ?? 8765);

createServer(async (_req, res) => {
  try {
    const html = await readFile(page);
    res.writeHead(200, { 'Content-Type': 'text/html; charset=utf-8', 'Cache-Control': 'no-store' });
    res.end(html);
  } catch {
    res.writeHead(503, { 'Content-Type': 'text/plain' });
    res.end('Progress page not written yet; it appears within a few seconds.');
  }
}).listen(port, '127.0.0.1', () => console.log(`progress page on http://127.0.0.1:${port}`));
