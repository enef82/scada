import { lazy, Suspense, useEffect } from 'react';
import { BrowserRouter, MemoryRouter, Navigate, Route, Routes } from 'react-router-dom';
import { DEMO } from './demo/flag';
import { Layout } from './components/Layout';
import { Empty } from './components/ui';
import { setUnauthorizedHandler } from './lib/api';
import { useAlarms } from './lib/alarms';
import { startRealtime, stopRealtime } from './lib/realtime';
import { useAreas, useAuth, useCan, useMeta } from './lib/stores';
import { AlarmDefsPage } from './pages/AlarmDefsPage';
import { AlarmsPage } from './pages/AlarmsPage';
import { AuditPage, DiagnosticsPage } from './pages/AdminPages';
import { AreasPage, AssignmentsPage, RolesPage, UsersPage } from './pages/AccessPages';
import { DevicesPage } from './pages/DevicesPage';
import { EventsPage } from './pages/EventsPage';
import { LoginPage } from './pages/LoginPage';
import { MnemosPage } from './pages/MnemosPage';
import { OperatorPage } from './pages/OperatorPage';
import { TagsPage } from './pages/TagsPage';
import { TrendsPage } from './pages/TrendsPage';
import { PublishPage } from './pages/PublishPage';
import type { Perm } from './lib/types';

const DemoPanel = DEMO ? lazy(() => import('./demo/DemoPanel')) : null;
// в демо адресная строка недоступна — маршруты живут в памяти
const Router = DEMO ? MemoryRouter : BrowserRouter;

function Guard({ perm, children }: { perm: Perm | Perm[]; children: React.ReactNode }) {
  return useCan(perm) ? <>{children}</> : <Empty>Недостаточно прав</Empty>;
}

function Authenticated() {
  const loaded = useMeta((s) => s.loaded);
  useEffect(() => {
    startRealtime();
    void useMeta.getState().reloadAll().catch(() => {});
    void useAlarms.getState().reload().catch(() => {});
    void useAreas.getState().reload().catch(() => {});
    return () => void stopRealtime();
  }, []);

  const me = useAuth((s) => s.me);
  if (!loaded) return <Empty>Загрузка конфигурации…</Empty>;
  if (me && Object.keys(me.permissions ?? {}).length === 0)
    return <Layout><Empty>У учётной записи {me.login} нет назначенных прав. Обратитесь к администратору безопасности.</Empty></Layout>;
  return (
    <Layout>
      <Routes>
        <Route path="/" element={<OperatorPage />} />
        <Route path="/mnemo/:id" element={<OperatorPage />} />
        <Route path="/alarms" element={<AlarmsPage />} />
        <Route path="/trends" element={<TrendsPage />} />
        <Route path="/events" element={<EventsPage />} />
        <Route path="/diagnostics" element={<DiagnosticsPage />} />
        <Route path="/eng/devices" element={<Guard perm="config.edit"><DevicesPage /></Guard>} />
        <Route path="/eng/tags" element={<Guard perm="config.edit"><TagsPage /></Guard>} />
        <Route path="/eng/alarms" element={<Guard perm="config.edit"><AlarmDefsPage /></Guard>} />
        <Route path="/eng/mnemos" element={<Guard perm="mnemo.edit"><MnemosPage /></Guard>} />
        <Route path="/eng/mnemos/:id" element={<Guard perm="mnemo.edit"><MnemosPage /></Guard>} />
        <Route path="/eng/publish" element={<Guard perm={['config.edit', 'mnemo.edit', 'config.publish']}><PublishPage /></Guard>} />
        <Route path="/eng/audit" element={<Guard perm="audit.view"><AuditPage /></Guard>} />
        <Route path="/admin/users" element={<Guard perm="users.manage"><UsersPage /></Guard>} />
        <Route path="/admin/assignments" element={<Guard perm="users.manage"><AssignmentsPage /></Guard>} />
        <Route path="/admin/roles" element={<Guard perm="users.manage"><RolesPage /></Guard>} />
        <Route path="/admin/areas" element={<Guard perm={['users.manage', 'config.edit']}><AreasPage /></Guard>} />
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
