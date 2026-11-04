import { lazy, Suspense, useEffect } from 'react';
import { BrowserRouter, MemoryRouter, Navigate, Route, Routes } from 'react-router-dom';
import { DEMO } from './demo/flag';
import { Layout } from './components/Layout';
import { Empty } from './components/ui';
import { setUnauthorizedHandler } from './lib/api';
import { useAlarms } from './lib/alarms';
import { startRealtime, stopRealtime } from './lib/realtime';
import { useAuth, useCan, useMeta } from './lib/stores';
import { AlarmDefsPage } from './pages/AlarmDefsPage';
import { AlarmsPage } from './pages/AlarmsPage';
import { AuditPage, DiagnosticsPage, UsersPage } from './pages/AdminPages';
import { DevicesPage } from './pages/DevicesPage';
import { EventsPage } from './pages/EventsPage';
import { LoginPage } from './pages/LoginPage';
import { MnemosPage } from './pages/MnemosPage';
import { OperatorPage } from './pages/OperatorPage';
import { TagsPage } from './pages/TagsPage';
import { TrendsPage } from './pages/TrendsPage';
import { PublishPage } from './pages/PublishPage';
import type { Role } from './lib/types';

const DemoPanel = DEMO ? lazy(() => import('./demo/DemoPanel')) : null;
// в демо адресная строка недоступна — маршруты живут в памяти
const Router = DEMO ? MemoryRouter : BrowserRouter;

function Guard({ role, children }: { role: Role; children: React.ReactNode }) {
  return useCan(role) ? <>{children}</> : <Empty>Недостаточно прав</Empty>;
}

function Authenticated() {
  const loaded = useMeta((s) => s.loaded);
  useEffect(() => {
    startRealtime();
    void useMeta.getState().reloadAll().catch(() => {});
    void useAlarms.getState().reload().catch(() => {});
    return () => void stopRealtime();
  }, []);

  if (!loaded) return <Empty>Загрузка конфигурации…</Empty>;
  return (
    <Layout>
      <Routes>
        <Route path="/" element={<OperatorPage />} />
        <Route path="/mnemo/:id" element={<OperatorPage />} />
        <Route path="/alarms" element={<AlarmsPage />} />
        <Route path="/trends" element={<TrendsPage />} />
        <Route path="/events" element={<EventsPage />} />
        <Route path="/diagnostics" element={<DiagnosticsPage />} />
        <Route path="/eng/devices" element={<Guard role="engineer"><DevicesPage /></Guard>} />
        <Route path="/eng/tags" element={<Guard role="engineer"><TagsPage /></Guard>} />
        <Route path="/eng/alarms" element={<Guard role="engineer"><AlarmDefsPage /></Guard>} />
        <Route path="/eng/mnemos" element={<Guard role="engineer"><MnemosPage /></Guard>} />
        <Route path="/eng/mnemos/:id" element={<Guard role="engineer"><MnemosPage /></Guard>} />
        <Route path="/eng/publish" element={<Guard role="engineer"><PublishPage /></Guard>} />
        <Route path="/eng/audit" element={<Guard role="engineer"><AuditPage /></Guard>} />
        <Route path="/admin/users" element={<Guard role="admin"><UsersPage /></Guard>} />
        <Route path="/login" element={<Navigate to="/" replace />} />
        <Route path="*" element={<Empty>Страница не найдена</Empty>} />
      </Routes>
    </Layout>
  );
}

export function App() {
  const me = useAuth((s) => s.me);
  const loaded = useAuth((s) => s.loaded);

  useEffect(() => {
    setUnauthorizedHandler(() => useAuth.setState({ me: null }));
    void useAuth.getState().load();
  }, []);

  if (!loaded) return <Empty>Загрузка…</Empty>;
  return (
    <Router>
      {me ? <Authenticated /> : (
        <Routes>
          <Route path="*" element={<LoginPage />} />
        </Routes>
      )}
      {DemoPanel && <Suspense fallback={null}><DemoPanel /></Suspense>}
    </Router>
  );
}
