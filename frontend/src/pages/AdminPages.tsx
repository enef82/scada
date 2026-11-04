import { useCallback, useEffect, useState } from 'react';
import { Badge, Empty, Field, Modal, askConfirm } from '../components/ui';
import { api, errorText, qs } from '../lib/api';
import { formatNumber, formatTime, roleNames, toLocalInput } from '../lib/format';
import { useRtStatus } from '../lib/realtime';
import { toast, useAuth, useCan, useMeta } from '../lib/stores';
import type { AuditRow, Role, UserRow } from '../lib/types';

// ------------------------------------------------------------------ пользователи
export function UsersPage() {
  const [rows, setRows] = useState<UserRow[]>([]);
  const [edit, setEdit] = useState<Partial<UserRow> | null>(null);
  const me = useAuth((s) => s.me);
  const load = useCallback(async () => {
    try { setRows(await api.get<UserRow[]>('/api/users')); } catch (e) { toast.error(errorText(e)); }
  }, []);
  useEffect(() => { void load(); }, [load]);

  const remove = async (u: UserRow) => {
    if (!(await askConfirm(`Удалить пользователя ${u.login}? Действует сразу.`, { ok: 'Удалить' }))) return;
    try { await api.del(`/api/users/${u.id}`); void load(); } catch (e) { toast.error(errorText(e)); }
  };

  return (
    <div className="page">
      <div className="toolbar">
        <h3>Пользователи</h3>
        <span className="grow" />
        <button className="primary" onClick={() => setEdit({ login: '', displayName: '', role: 'operator', enabled: true })}>Добавить</button>
      </div>
      <table className="table">
        <thead><tr><th>Логин</th><th>Имя</th><th>Роль</th><th>Активен</th><th>Последний вход</th><th /></tr></thead>
        <tbody>
          {rows.map((u) => (
            <tr key={u.id} className={u.enabled ? '' : 'row-disabled'}>
              <td><b>{u.login}</b>{u.mustChangePassword && <Badge kind="warn">смена пароля</Badge>}</td>
              <td>{u.displayName}</td>
              <td>{roleNames[u.role]}</td>
              <td>{u.enabled ? 'да' : 'нет'}</td>
              <td>{formatTime(u.lastLoginAt)}</td>
              <td className="actions">
                <button onClick={() => setEdit(u)}>Изменить</button>
                <button disabled={u.login === me?.login} onClick={() => void remove(u)}>×</button>
              </td>
            </tr>
          ))}
        </tbody>
      </table>
      {edit && <UserForm u={edit} onClose={() => setEdit(null)} onSaved={() => { setEdit(null); void load(); }} />}
    </div>
  );
}

function UserForm({ u, onClose, onSaved }: { u: Partial<UserRow>; onClose: () => void; onSaved: () => void }) {
  const [login, setLogin] = useState(u.login ?? '');
  const [displayName, setName] = useState(u.displayName ?? '');
  const [role, setRole] = useState<Role>(u.role ?? 'operator');
  const [enabled, setEnabled] = useState(u.enabled ?? true);
  const [password, setPassword] = useState('');
  const save = async () => {
    try {
      const body = { login, displayName, role, enabled, password: password || null };
      if (u.id) await api.put(`/api/users/${u.id}`, body); else await api.post('/api/users', body);
      toast.ok('Сохранено');
      onSaved();
    } catch (e) { toast.error(errorText(e)); }
  };
  return (
    <Modal title={u.id ? `Пользователь ${u.login}` : 'Новый пользователь'} onClose={onClose}
      footer={<><button onClick={onClose}>Отмена</button><button className="primary" onClick={save}>Сохранить</button></>}>
      <div className="form">
        <Field label="Логин"><input value={login} disabled={!!u.id} onChange={(e) => setLogin(e.target.value)} /></Field>
        <Field label="Имя (ФИО)"><input value={displayName} onChange={(e) => setName(e.target.value)} /></Field>
        <Field label="Роль">
          <select value={role} onChange={(e) => setRole(e.target.value as Role)}>
            {Object.entries(roleNames).map(([k, v]) => <option key={k} value={k}>{v}</option>)}
          </select>
        </Field>
        <Field label="Активен"><input type="checkbox" checked={enabled} onChange={(e) => setEnabled(e.target.checked)} /></Field>
        <Field label={u.id ? 'Новый пароль (сброс)' : 'Пароль'} hint="не короче 8 символов; при первом входе потребуется смена" wide>
          <input type="password" value={password} onChange={(e) => setPassword(e.target.value)} />
        </Field>
      </div>
      <p className="muted small">
        Наблюдатель — только просмотр. Оператор — команды и квитирование. Инженер — конфигурация и схемы. Администратор — пользователи.
      </p>
    </Modal>
  );
}

// ------------------------------------------------------------------ аудит
const actionNames: Record<string, string> = {
  login: 'Вход', login_failed: 'Неудачный вход', logout: 'Выход', password_change: 'Смена пароля',
  config_change: 'Изменение конфигурации', command: 'Команда', alarm_ack: 'Квитирование', alarm_shelve: 'Снятие с контроля',
  alarm_unshelve: 'Возврат на контроль', import: 'Импорт', export: 'Экспорт', user_change: 'Пользователи',
};

export function AuditPage() {
  const isAdmin = useCan('admin');
  const [from, setFrom] = useState(() => toLocalInput(new Date(Date.now() - 7 * 86400_000)));
  const [to, setTo] = useState(() => toLocalInput(new Date(Date.now() + 60_000)));
  const [action, setAction] = useState('');
  const [rows, setRows] = useState<AuditRow[]>([]);
  const load = useCallback(async () => {
    try {
      setRows(await api.get<AuditRow[]>(`/api/audit${qs({ from: new Date(from).toISOString(), to: new Date(to).toISOString(), action, limit: 2000 })}`));
    } catch (e) { toast.error(errorText(e)); }
  }, [from, to, action]);
  useEffect(() => { void load(); }, [load]);

  const verify = async () => {
    try {
      const r = await api.get<{ intact: boolean; firstBrokenId: number | null }>('/api/audit/verify');
      if (r.intact) toast.ok('Цепочка хешей журнала аудита цела');
      else toast.error(`Нарушена целостность журнала начиная с записи №${r.firstBrokenId}`);
    } catch (e) { toast.error(errorText(e)); }
  };

  return (
    <div className="page">
      <div className="toolbar">
        <h3>Журнал аудита</h3>
        <label>С <input type="datetime-local" value={from} onChange={(e) => setFrom(e.target.value)} /></label>
        <label>По <input type="datetime-local" value={to} onChange={(e) => setTo(e.target.value)} /></label>
        <select value={action} onChange={(e) => setAction(e.target.value)}>
          <option value="">все действия</option>
          {Object.entries(actionNames).map(([k, v]) => <option key={k} value={k}>{v}</option>)}
        </select>
        <span className="grow" />
        {isAdmin && <button onClick={() => void verify()}>Проверить целостность</button>}
      </div>
      {rows.length === 0 ? <Empty>Записей нет</Empty> : (
        <table className="table">
          <thead><tr><th>№</th><th>Время</th><th>Пользователь</th><th>Действие</th><th>Объект</th><th>Подробности</th><th>IP</th></tr></thead>
          <tbody>
            {rows.map((r) => (
              <tr key={r.id} className={r.action === 'login_failed' ? 'row-warn' : ''}>
                <td>{r.id}</td>
                <td>{formatTime(r.ts)}</td>
                <td>{r.user}</td>
                <td>{actionNames[r.action] ?? r.action}</td>
                <td className="mono small">{r.object}</td>
                <td className="mono small details">{JSON.stringify(r.details)}</td>
                <td className="small">{r.ip}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </div>
  );
}

// ------------------------------------------------------------------ диагностика
interface Diag {
  web: { listenerConnected: boolean; cachedValues: number; connections: number; version: string };
  acquisition: { lastHeartbeatAt: string | null; heartbeat: { tags: number; devices: number; historianQueue: number; spoolBytes: number } | null };
  db: { dbBytes: number; tags: number; devices: number; activeAlarms: number; historyPartitions: number };
}

export function DiagnosticsPage() {
  const [d, setD] = useState<Diag | null>(null);
  const devices = useMeta((s) => s.devices);
  const link = useRtStatus((s) => s.link);
  useEffect(() => {
    const load = () => void api.get<Diag>('/api/diagnostics').then(setD).catch(() => {});
    load();
    const t = setInterval(load, 5000);
    return () => clearInterval(t);
  }, []);
  if (!d) return <Empty>Загрузка…</Empty>;
  const hbAge = d.acquisition.lastHeartbeatAt ? (Date.now() - new Date(d.acquisition.lastHeartbeatAt).getTime()) / 1000 : null;
  return (
    <div className="page">
      <h3>Диагностика</h3>
      <div className="cards">
        <div className="card">
          <h4>Служба сбора</h4>
          <Badge kind={hbAge !== null && hbAge < 20 ? 'ok' : 'bad'}>{hbAge === null ? 'нет данных' : hbAge < 20 ? 'работает' : `нет ответа ${Math.round(hbAge)} с`}</Badge>
          <dl>
            <dt>Тегов</dt><dd>{d.acquisition.heartbeat?.tags ?? '—'}</dd>
            <dt>Устройств в опросе</dt><dd>{d.acquisition.heartbeat?.devices ?? '—'}</dd>
            <dt>Очередь архива</dt><dd>{d.acquisition.heartbeat?.historianQueue ?? '—'}</dd>
            <dt>Дисковый буфер</dt><dd>{d.acquisition.heartbeat ? `${formatNumber(d.acquisition.heartbeat.spoolBytes / 1048576, 1)} МБ` : '—'}</dd>
          </dl>
        </div>
        <div className="card">
          <h4>Веб-сервер</h4>
          <dl>
            <dt>LISTEN к БД</dt><dd>{d.web.listenerConnected ? 'подключён' : 'нет'}</dd>
            <dt>Значений в кэше</dt><dd>{d.web.cachedValues}</dd>
            <dt>Клиентов онлайн</dt><dd>{d.web.connections}</dd>
            <dt>Этот браузер</dt><dd>{link}</dd>
            <dt>Версия</dt><dd>{d.web.version}</dd>
          </dl>
        </div>
        <div className="card">
          <h4>База данных</h4>
          <dl>
            <dt>Размер</dt><dd>{formatNumber(d.db.dbBytes / 1048576, 0)} МБ</dd>
            <dt>Тегов / устройств</dt><dd>{d.db.tags} / {d.db.devices}</dd>
            <dt>Активных тревог</dt><dd>{d.db.activeAlarms}</dd>
            <dt>Секций архива</dt><dd>{d.db.historyPartitions}</dd>
          </dl>
        </div>
      </div>
      <h4>Каналы связи</h4>
      <table className="table">
        <thead><tr><th>Устройство</th><th>Состояние</th><th>Последний обмен</th><th>Задержка</th><th>Ошибки</th><th>Последняя ошибка</th></tr></thead>
        <tbody>
          {devices.map((x) => (
            <tr key={x.id}>
              <td>{x.name}</td>
              <td><Badge kind={!x.enabled ? 'muted' : x.state === 'online' ? 'ok' : 'bad'}>{x.enabled ? x.state ?? '—' : 'отключено'}</Badge></td>
              <td>{formatTime(x.lastOkAt)}</td>
              <td>{x.avgLatencyMs ? `${formatNumber(x.avgLatencyMs, 1)} мс` : ''}</td>
              <td>{x.errorCount}</td>
              <td className="small">{x.lastError}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}
