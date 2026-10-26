// 纯 Node（内置模块）静态文件服务器 + 反向代理；无需 npm 依赖。
// 由 pack-bundle.ps1 复制到包内 frontend/serve.mjs；运行时用包内 node/node.exe 启动。
// 环境变量：FRONTEND_PORT(默认 4171) GATEWAY_TARGET HOST_TARGET
import http from 'node:http';
import net from 'node:net';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const DIST = path.join(__dirname, 'dist');
const PORT = Number(process.env.FRONTEND_PORT || 4171);
const ROUTES = [
  { prefix: '/machine-connection', target: process.env.GATEWAY_TARGET || 'http://localhost:5087', ws: false },
  { prefix: '/industrial-iot', target: process.env.HOST_TARGET || 'http://localhost:5173', ws: true },
];
const MIME = { '.html':'text/html; charset=utf-8', '.js':'text/javascript', '.mjs':'text/javascript', '.css':'text/css', '.json':'application/json', '.svg':'image/svg+xml', '.png':'image/png', '.jpg':'image/jpeg', '.jpeg':'image/jpeg', '.gif':'image/gif', '.ico':'image/x-icon', '.woff':'font/woff', '.woff2':'font/woff2', '.map':'application/json', '.txt':'text/plain' };
const routeOf = (u) => ROUTES.find(r => u.startsWith(r.prefix));
// Windows 上 Node 会把 localhost 先解析为 IPv6 ::1，而后端常只监听 IPv4(0.0.0.0)，故统一用 127.0.0.1。
function target(t) { const u = new URL(t); let h = u.hostname; if (h === 'localhost' || h === '::1') h = '127.0.0.1'; return { host: h, port: Number(u.port || 80) }; }

const server = http.createServer((req, res) => {
  const route = routeOf(req.url);
  if (route) {
    const t = target(route.target);
    const p = req.url.slice(route.prefix.length) || '/';
    const headers = { ...req.headers, host: t.host + ':' + t.port };
    const pr = http.request({ host: t.host, port: t.port, path: p, method: req.method, headers }, (up) => {
      res.writeHead(up.statusCode, up.headers); up.pipe(res);
    });
    pr.on('error', (e) => { res.writeHead(502); res.end('proxy error: ' + e.message); });
    req.pipe(pr);
    return;
  }
  let fp = path.join(DIST, decodeURIComponent((req.url || '/').split('?')[0]));
  if (!fp.startsWith(DIST)) { res.writeHead(403); res.end('forbidden'); return; }
  fs.stat(fp, (err, st) => {
    if (err || st.isDirectory()) fp = path.join(DIST, 'index.html');
    fs.readFile(fp, (e, data) => {
      if (e) { res.writeHead(404); res.end('not found'); return; }
      res.writeHead(200, { 'content-type': MIME[path.extname(fp).toLowerCase()] || 'application/octet-stream' });
      res.end(data);
    });
  });
});

server.on('upgrade', (req, socket, head) => {
  const route = routeOf(req.url);
  if (!route || !route.ws) { socket.destroy(); return; }
  const t = target(route.target);
  const p = req.url.slice(route.prefix.length) || '/';
  const up = net.connect(t.port, t.host, () => {
    const lines = Object.entries({ ...req.headers, host: t.host + ':' + t.port }).map(([k, v]) => k + ': ' + v).join('\r\n');
    up.write(req.method + ' ' + p + ' HTTP/1.1\r\n' + lines + '\r\n\r\n');
    if (head && head.length) up.write(head);
    socket.pipe(up); up.pipe(socket);
  });
  up.on('error', () => socket.destroy());
});

server.listen(PORT, () => console.log('frontend on http://0.0.0.0:' + PORT + '  proxy -> ' + ROUTES.map(r => r.prefix + '=' + r.target).join(', ')));
