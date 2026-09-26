// Minimal static server for the published WASM app. Serves precompressed .br/.gz
// siblings when the client accepts them, and maps the WASM/js/html MIME types the
// dotnet loader requires. Usage: node serve.mjs [port] [root]
import { createServer } from 'node:http';
import { createReadStream, existsSync, statSync } from 'node:fs';
import { join, extname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const port = Number(process.argv[2]) || 8756;
const root = resolve(process.argv[3] || 'bin/Release/net10.0/publish/wwwroot');
// The game GLBs live next to the core project; fileURLToPath keeps Windows
// drive letters intact (a URL pathname is "/C:/...", not a valid path).
const assetRoot = resolve(fileURLToPath(new URL('../SafariPark.Core/Assets', import.meta.url)));

const mime = {
    '.html': 'text/html; charset=utf-8',
    '.js': 'text/javascript; charset=utf-8',
    '.mjs': 'text/javascript; charset=utf-8',
    '.wasm': 'application/wasm',
    '.json': 'application/json',
    '.css': 'text/css',
    '.png': 'image/png',
    '.ico': 'image/x-icon',
    '.map': 'application/json',
    '.dat': 'application/octet-stream',
    '.glb': 'model/gltf-binary',
    '.symbols': 'application/octet-stream',
};

createServer((req, res) => {
    try {
        let path = decodeURIComponent(new URL(req.url, 'http://x').pathname);
        if (path.endsWith('/')) path += 'index.html';
        const file = path.startsWith('/assets/')
            ? join(assetRoot, path.slice('/assets/'.length))
            : join(root, path);
        const allowed = path.startsWith('/assets/') ? assetRoot : root;
        if (!file.startsWith(allowed) || !existsSync(file) || !statSync(file).isFile()) {
            res.writeHead(404); res.end('not found'); return;
        }
        const accept = req.headers['accept-encoding'] || '';
        let target = file, encoding = null;
        if (accept.includes('br') && existsSync(file + '.br')) { target = file + '.br'; encoding = 'br'; }
        else if (accept.includes('gzip') && existsSync(file + '.gz')) { target = file + '.gz'; encoding = 'gzip'; }

        const headers = {
            'Content-Type': mime[extname(file)] || 'application/octet-stream',
            'Cache-Control': 'no-cache',
        };
        if (encoding) headers['Content-Encoding'] = encoding;
        res.writeHead(200, headers);
        createReadStream(target).pipe(res);
    } catch (e) {
        res.writeHead(500); res.end(String(e));
    }
}).listen(port, () => console.log(`serving ${root} on http://127.0.0.1:${port}`));
