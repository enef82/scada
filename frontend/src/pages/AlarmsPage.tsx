import { useCallback, useEffect, useState } from 'react';
import { Empty, Field, Modal } from '../components/ui';
import { api, errorText, qs } from '../lib/api';
import { useAlarms } from '../lib/alarms';
import { formatNumber, formatTime, priorityNames, toLocalInput } from '../lib/format';
import { useRealtimeEvent } from '../lib/realtime';
import { toast, usePerms , useMeta} from '../lib/stores';
import type { ActiveAlarm, AlarmEvent } from '../lib/types';

function stateText(a: ActiveAlarm): string {
  if (a.shelvedUntil && new Date(a.shelvedUntil) > new Date()) return 'Снята с контроля';
  if (a.active && !a.acked) return 'Активна, не квитирована';
  if (a.active) return 'Активна';
  if (!a.acked) return 'Норма, не квитирована';
  return 'Норма';
}

export function AlarmsPage() {
  const [tab, setTab] = useState<'active' | 'history'>('active');
  return (
    <div className="page">
      <div className="tabs">
        <button className={tab === 'active' ? 'active' : ''} onClick={() => setTab('active')}>Текущие</button>
        <button className={tab === 'history' ? 'active' : ''} onClick={() => setTab('history')}>Журнал тревог</button>
      </div>
      {tab === 'active' ? <ActiveAlarms /> : <AlarmHistory />}
    </div>
  );
}

function ActiveAlarms() {
  const active = useAlarms((s) => s.active);
  const reload = useAlarms((s) => s.reload);
  const perms = usePerms();
  const tagById = useMeta((s) => s.tagById);
  // квитирование и снятие с контроля — по области тревоги (= области тега)
  const can = (perm: 'alarm.ack' | 'alarm.shelve', a: { tagId: number }) => perms(perm, tagById.get(a.tagId)?.effectiveAreaId ?? 0);
  const canAll = (perm: 'alarm.ack' | 'alarm.shelve', ids: number[]) =>
    ids.length > 0 && ids.every((id) => { const a = active.find((x) => x.id === id); return !!a && can(perm, a); });
  const [sel, setSel] = useState<Set<number>>(new Set());
  const [shelve, setShelve] = useState<number[] | null>(null);

  useEffect(() => { void reload().catch(() => {}); }, [reload]);

  const act = async (url: string, ids: number[], body: object = {}) => {
    if (!ids.length) return;
    try {
      await api.post(url, { ids, ...body });
      setSel(new Set());
    } catch (e) {
      toast.error(errorText(e));
    }
  };

  const unackedIds = active.filter((a) => !a.acked && can('alarm.ack', a)).map((a) => a.id);
  const toggle = (id: number) => setSel((s) => { const n = new Set(s); if (n.has(id)) n.delete(id); else n.add(id); return n; });

  return (
    <>
      <div className="toolbar">
        <button className="primary" disabled={!unackedIds.length} onClick={() => void act('/api/alarms/ack', unackedIds)}>
          Квитировать все ({unackedIds.length})
        </button>
        <button disabled={!canAll('alarm.ack', [...sel])} onClick={() => void act('/api/alarms/ack', [...sel])}>Квитировать выбранные</button>
        <button disabled={!canAll('alarm.shelve', [...sel])} title={perms('alarm.shelve') ? '' : 'Нужно право «Снятие с контроля» (старший оператор)'} onClick={() => setShelve([...sel])}>Снять с контроля…</button>
        <button disabled={!canAll('alarm.shelve', [...sel])} onClick={() => void act('/api/alarms/unshelve', [...sel])}>Вернуть на контроль</button>
        <span className="grow" />
        <span className="muted">Всего: {active.length}</span>
      </div>
      {active.length === 0 ? <Empty>Активных тревог нет</Empty> : (
        <table className="table alarms-table">
          <thead>
            <tr><th /><th>Приоритет</th><th>Время</th><th>Тег</th><th>Сообщение</th><th>Значение</th><th>Состояние</th><th>Квитировал</th><th /></tr>
          </thead>
          <tbody>
            {active.map((a) => (
              <tr key={a.id} className={`arow arow-p${a.priority} ${a.active ? 'arow-active' : ''} ${!a.acked ? 'arow-unacked' : ''}`}>
                <td><input type="checkbox" checked={sel.has(a.id)} onChange={() => toggle(a.id)} /></td>
                <td><span className={`prio prio-${a.priority}`}>{priorityNames[a.priority]}</span></td>
                <td>{formatTime(a.raisedAt)}</td>
                <td title={a.tagDescription}><b>{a.tagName}</b></td>
                <td>{a.message}{a.comment && <div className="muted small">{a.comment}</div>}</td>
                <td>{formatNumber(a.value)} {a.unit}</td>
                <td>{stateText(a)}</td>
                <td>{a.ackedBy ? `${a.ackedBy}, ${formatTime(a.ackedAt, false)}` : ''}</td>
                <td>{!a.acked && <button disabled={!can('alarm.ack', a)} onClick={() => void act('/api/alarms/ack', [a.id])}>Квитировать</button>}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
      {shelve && <ShelveDialog ids={shelve} onClose={() => setShelve(null)}
        onSubmit={(minutes, comment) => { void act('/api/alarms/shelve', shelve, { minutes, comment }); setShelve(null); }} />}
    </>
  );
}

function ShelveDialog(props: { ids: number[]; onClose: () => void; onSubmit: (m: number, c: string) => void }) {
  const [minutes, setMinutes] = useState(60);
  const [comment, setComment] = useState('');
  return (
    <Modal title={`Снять с контроля (${props.ids.length})`} onClose={props.onClose}
      footer={<><button onClick={props.onClose}>Отмена</button>
        <button className="primary" disabled={!comment.trim()} onClick={() => props.onSubmit(minutes, comment)}>Снять</button></>}>
      <div className="form">
        <Field label="На время">
          <select value={minutes} onChange={(e) => setMinutes(Number(e.target.value))}>
            {[15, 30, 60, 120, 240, 480, 1440].map((m) => <option key={m} value={m}>{m < 60 ? `${m} мин` : `${m / 60} ч`}</option>)}
          </select>
        </Field>
        <Field label="Причина (обязательно)" wide><input value={comment} onChange={(e) => setComment(e.target.value)} /></Field>
      </div>
      <p className="muted small">Снятая с контроля тревога не сигнализирует до окончания срока. Действие фиксируется в аудите.</p>
    </Modal>
  );
}

const eventNames: Record<string, string> = {
  raise: 'Возникла', clear: 'Норма', ack: 'Квитирована', shelve: 'Снята с контроля', unshelve: 'Возвращена', comment: 'Комментарий',
};

function AlarmHistory() {
  const [from, setFrom] = useState(() => toLocalInput(new Date(Date.now() - 24 * 3600_000)));
  const [to, setTo] = useState(() => toLocalInput(new Date(Date.now() + 60_000)));
  const [rows, setRows] = useState<AlarmEvent[]>([]);
  const load = useCallback(async () => {
    try {
      setRows(await api.get<AlarmEvent[]>(`/api/alarms/events${qs({ from: new Date(from).toISOString(), to: new Date(to).toISOString(), limit: 2000 })}`));
    } catch (e) {
      toast.error(errorText(e));
    }
  }, [from, to]);
  useEffect(() => { void load(); }, [load]);
  useRealtimeEvent('alarm', useCallback(() => void load(), [load]));

  return (
    <>
      <div className="toolbar">
        <label>С <input type="datetime-local" value={from} onChange={(e) => setFrom(e.target.value)} /></label>
        <label>По <input type="datetime-local" value={to} onChange={(e) => setTo(e.target.value)} /></label>
        <button onClick={() => void load()}>Обновить</button>
      </div>
      <table className="table">
        <thead><tr><th>Время</th><th>Событие</th><th>Приоритет</th><th>Тег</th><th>Сообщение</th><th>Значение</th><th>Пользователь</th><th>Комментарий</th></tr></thead>
        <tbody>
          {rows.map((r) => (
            <tr key={r.id}>
              <td>{formatTime(r.ts)}</td>
              <td>{eventNames[r.event] ?? r.event}</td>
              <td><span className={`prio prio-${r.priority}`}>{priorityNames[r.priority]}</span></td>
              <td>{r.tagName}</td>
              <td>{r.message}</td>
              <td>{formatNumber(r.value)}</td>
              <td>{r.user}</td>
              <td>{r.comment}</td>
            </tr>
          ))}
        </tbody>
      </table>
      {rows.length === 0 && <Empty>Событий за период нет</Empty>}
    </>
  );
}
