import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { App } from './App';
import { DEMO } from './demo/flag';
import './styles.css';

async function boot() {
  if (DEMO) {
    const [{ initDemo, transport }, { setDemoTransport }, { toast }] = await Promise.all([
      import('./demo/sim'), import('./lib/realtime'), import('./lib/stores'),
    ]);
    initDemo();
    setDemoTransport(transport);
    // выгрузка файлов в демо не работает — объясняем вместо пустого клика
    document.addEventListener('click', (e) => {
      const a = (e.target as Element | null)?.closest?.('a[href^="/api/"]');
      if (!a) return;
      e.preventDefault();
      toast.info('В прототипе выгрузка CSV отключена. В рабочей системе здесь скачивается файл.');
    }, true);
  }
  createRoot(document.getElementById('root')!).render(
    <StrictMode>
      <App />
    </StrictMode>,
  );
}

void boot();
