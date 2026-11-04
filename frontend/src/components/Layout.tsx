import { useState, type ReactNode } from 'react';
import { NavLink, useNavigate } from 'react-router-dom';
import { api, errorText } from '../lib/api';
import { useAlarms } from '../lib/alarms';
import { formatTime, priorityNames } from '../lib/format';
import { useAcquisitionAlive, useNow, useRealtimeEvent, useRtStatus } from '../lib/realtime';
import { toast, useAuth, useCan, useDraft } from '../lib/stores';
import { CommandDialog, ConfirmRequests } from './CommandDialog';
import { ConfirmHost, Field, Modal, Toasts } from './ui';
import { useEffect } from 'react';

export function Layout({ children }: { children: ReactNode }) {
  const me = useAuth((s) => s.me);
  const logout = useAuth((s) => s.logout);
  const canConfig = useCan('config.edit');
  const canMnemo = useCan('mnemo.edit');
  const canEng = useCan(['config.edit', 'mnemo.edit', 'config.publish']);
  const canAudit = useCan('audit.view');
  const canAccess = useCan(['users.manage', 'config.edit']);
  const canUsers = useCan('users.manage');
  // системные оповещения (вход аварийной учётной записью и т. п.)
  useRealtimeEvent('systemNotice', (p) => {
    const n = p as { level: string; text: string };
    if (canAudit || canUsers) toast.error(n.text);
  });
  const navigate = useNavigate();
  const [pwd, setPwd] = useState(false);

  return (
    <div className="app">
      <header className="topbar">
        <div className="brand" onClick={() => navigate('/')}>SCADA</div>
        <nav className="nav">
          <NavLink to="/" end>Мнемосхемы</NavLink>
          <NavLink to="/alarms">Тревоги</NavLink>
          <NavLink to="/trends">Тренды</NavLink>
          <NavLink to="/events">Журналы</NavLink>
          {(canEng || canAudit || canAccess) && <span className="nav-sep" />}
          {canConfig && <NavLink to="/eng/devices">Устройства</NavLink>}
          {canConfig && <NavLink to="/eng/tags">Теги</NavLink>}
          {canConfig && <NavLink to="/eng/alarms">Уставки тревог</NavLink>}
          {canMnemo && <NavLink to="/eng/mnemos">Редактор схем</NavLink>}
          {canEng && <NavLink to="/eng/publish">Публикация</NavLink>}
          {canAudit && <NavLink to="/eng/audit">Аудит</NavLink>}
          {canAccess && <NavLink to={canUsers ? '/admin/users' : '/admin/areas'}>Доступ</NavLink>}
          <NavLink to="/diagnostics">Диагностика</NavLink>
        </nav>
        {canEng && <DraftPlaque />}
        <StatusIndicators />
        <Clock />
        <div className="user" title={`${me?.displayName ?? ''}\n${(me?.roles ?? []).join(', ') || 'нет назначенных ролей'}${me?.groups?.length ? `\nГруппы AD: ${me.groups.join(', ')}` : ''}`}>
          <button className="link-btn" onClick={() => me?.source === 'ad' ? toast.info('Пароль доменной учётной записи меняется в Windows') : setPwd(true)}>{me?.login}</button>
          <small>{me?.emergency ? <span className="pill pill-bad">аварийная</span> : me?.roles?.length === 1 ? me.roles[0] : me?.roles?.length ? `ролей: ${me.roles.length}` : 'нет прав'}</small>
          <button className="link-btn" onClick={() => void logout().then(() => navigate('/login'))}>Выход</button>
        </div>
      </header>
      <main className="main">{children}</main>
      <AlarmBanner />
      <CommandDialog />
      <ConfirmRequests />
      <ConfirmHost />
      <Toasts />
      {(pwd || me?.mustChangePassword) && <ChangePassword forced={!!me?.mustChangePassword} onClose={() => setPwd(false)} />}
    </div>
  );
}

/** Плашка черновика: сколько неопубликованных правок и какая версия работает на объекте. */
function DraftPlaque() {
  const status = useDraft((s) => s.status);
  const reload = useDraft((s) => s.reload);
  const navigate = useNavigate();
  useEffect(() => { void reload(); }, [reload]);
  if (!status) return null;
  const n = status.draftChanges;
  return (
    <button className={`draft-plaque ${n ? 'draft-dirty' : ''}`} onClick={() => navigate('/eng/publish')}
      title={n ? `Правки не опубликованы. Авторы: ${status.draftAuthors.join(', ')}` : 'Черновик совпадает с работающей версией'}>
      {n ? `Черновик: ${n} ${plural(n, 'изменение', 'изменения', 'изменений')}` : 'Черновик пуст'}
      <small>в работе в.{status.publishedVersion}</small>
    </button>
  );
}

export function plural(n: number, one: string, few: string, many: string) {
  const m10 = n % 10, m100 = n % 100;
  if (m10 === 1 && m100 !== 11) return one;
  if (m10 >= 2 && m10 <= 4 && (m100 < 12 || m100 > 14)) return few;
  return many;
}

function Clock() {
  const now = useNow(1000);
  return <div className="clock">{formatTime(now)}</div>;
}

function StatusIndicators() {
  const link = useRtStatus((s) => s.link);
  const alive = useAcquisitionAlive();
  const linkText = { connected: 'Связь', connecting: 'Подключение…', reconnecting: 'Переподключение…', disconnected: 'Нет связи' }[link];
  return (
    <div className="status">
      <span className={`pill ${link === 'connected' ? 'pill-ok' : 'pill-bad blink'}`} title="Связь браузера с сервером SCADA">{linkText}</span>
      <span className={`pill ${alive ? 'pill-ok' : 'pill-bad'}`} title="Служба сбора данных (heartbeat)">
        {alive ? 'Сбор' : 'Сбор: нет'}
      </span>
    </div>
  );
}

/** Нижняя строка тревог: счётчики по приоритетам и последняя неквитированная тревога. */
function AlarmBanner() {
  const active = useAlarms((s) => s.active);
  const muted = useAlarms((s) => s.muted);
  const setMuted = useAlarms((s) => s.setMuted);
  const navigate = useNavigate();
  const unacked = active.filter((a) => !a.acked);
  const latest = [...unacked].sort((a, b) => a.priority - b.priority || (b.raisedAt ?? '').localeCompare(a.raisedAt ?? ''))[0];
  const counts = [1, 2, 3, 4].map((p) => active.filter((a) => a.priority === p && (a.active || !a.acked)).length);

  return (
    <footer className={`alarmbar ${latest ? `alarmbar-p${latest.priority}` : ''}`}>
      <div className="alarm-counts" onClick={() => navigate('/alarms')}>
        {counts.map((c, i) => (
          <span key={i} className={`acount acount-p${i + 1} ${c ? '' : 'zero'}`} title={priorityNames[i + 1]}>{c}</span>
        ))}
      </div>
      <div className="alarm-latest" onClick={() => navigate('/alarms')}>
        {latest ? (
          <>
            <span className={!latest.acked ? 'blink' : ''}>●</span> {formatTime(latest.raisedAt, false)} · <b>{latest.tagName}</b> · {latest.message}
            {!latest.active && <i> (вернулась в норму)</i>}
            {unacked.length > 1 && <span className="muted"> · ещё неквитированных: {unacked.length - 1}</span>}
          </>
        ) : (
          <span className="muted">
            {active.some((a) => a.active) ? `Все тревоги квитированы · активных: ${active.filter((a) => a.active).length}` : 'Нет активных тревог'}
          </span>
        )}
      </div>
      <button className="link-btn" onClick={() => setMuted(!muted)} title="Звуковая сигнализация">
        {muted ? '🔇 звук выкл.' : '🔊 звук'}
      </button>
    </footer>
  );
}

function ChangePassword({ forced, onClose }: { forced: boolean; onClose: () => void }) {
  const [oldPassword, setOld] = useState('');
  const [newPassword, setNew] = useState('');
  const [repeat, setRepeat] = useState('');
  const setMustChange = useAuth((s) => s.setMustChange);
  const submit = async () => {
    if (newPassword !== repeat) return toast.error('Пароли не совпадают');
    try {
      await api.post('/api/auth/change-password', { oldPassword, newPassword });
      toast.ok('Пароль изменён');
      setMustChange(false);
      onClose();
    } catch (e) {
      toast.error(errorText(e));
    }
  };
  return (
    <Modal title={forced ? 'Требуется смена пароля' : 'Смена пароля'} onClose={forced ? () => {} : onClose}
      footer={<>{!forced && <button onClick={onClose}>Отмена</button>}<button className="primary" onClick={submit}>Сменить</button></>}>
      {forced && <p>Это первый вход или пароль был сброшен администратором. Задайте новый пароль.</p>}
      <div className="form">
        <Field label="Текущий пароль"><input type="password" value={oldPassword} onChange={(e) => setOld(e.target.value)} /></Field>
        <Field label="Новый пароль" hint="не короче 8 символов"><input type="password" value={newPassword} onChange={(e) => setNew(e.target.value)} /></Field>
        <Field label="Повтор"><input type="password" value={repeat} onChange={(e) => setRepeat(e.target.value)} /></Field>
      </div>
    </Modal>
  );
}
