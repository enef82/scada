// Собирает dist-demo в один HTML-фрагмент для публикации (стили и скрипт встроены).
import fs from 'node:fs';
import path from 'node:path';

const dir = path.resolve('dist-demo');
const html = fs.readFileSync(path.join(dir, 'index.html'), 'utf8');
const css = [...html.matchAll(/href="\/?(assets\/[^"]+\.css)"/g)].map((m) => fs.readFileSync(path.join(dir, m[1]), 'utf8')).join('\n');
const js = [...html.matchAll(/src="\/?(assets\/[^"]+\.js)"/g)].map((m) => fs.readFileSync(path.join(dir, m[1]), 'utf8')).join('\n');
const icon = html.match(/<link rel="icon"[^>]*>/)?.[0] ?? '';
const out = `<title>Прототип АРМ SCADA</title>
${icon}
<style>${css}</style>
<div id="root"></div>
<script type="module">${js.replace(/<\/script/gi, '<\\/script')}</script>
`;
fs.writeFileSync(path.join(dir, 'scada-prototype.html'), out);
console.log('scada-prototype.html', (out.length / 1024).toFixed(0), 'KB');
