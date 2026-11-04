import { useCallback, useEffect, useState } from 'react';
import { Badge, Empty } from '../components/ui';
import { api, errorText } from '../lib/api';
import { commandStatusNames, formatNumber, formatTime } from '../lib/format';
import { useRealtimeEvent } from '../lib/realtime';
import { toast } from '../lib/stores';
import type { CommandRow } from '../lib/types';

const kind = (s: string) => (s === 'done' ? 'ok' : s === 'pending' || s === 'executing' ? 'info' : 'bad');

/** Журнал команд управления: кто, когда, что записал и чем закончилось. */
export function EventsPage() {
  const [rows, setRows] = useState<CommandRow[]>([]);
  const load = useCallback(async () => {
    try { setRows(await api.get<CommandRow[]>('/api/commands?limit=500')); }
    catch (e) { toast.error(errorText(e)); }
  }, []);
  useEffect(() => { void load(); }, [load]);
  useRealtimeEvent('command', useCallback(() => void load(), [load]));

  return (
    <div className="page">
      <h3>Журнал команд</h3>
      {rows.length === 0 ? <Empty>Команд ещё не было</Empty> : (
        <table className="table">
          <thead>
            <tr><th>№</th><th>Время</th><th>Оператор</th><th>Тег</th><th>Значение</th><th>Статус</th><th>Результат</th><th>Контр. чтение</th><th>Завершена</th></tr>
          </thead>
          <tbody>
            {rows.map((r) => (
              <tr key={r.id}>
                <td>{r.id}</td>
                <td>{formatTime(r.requestedAt)}</td>
                <td>{r.requestedBy}</td>
                <td><b>{r.tagName}</b></td>
                <td>{r.valueStr ?? formatNumber(r.valueNum)}</td>
                <td><Badge kind={kind(r.status)}>{commandStatusNames[r.status] ?? r.status}</Badge></td>
                <td>{r.result}</td>
                <td>{r.readbackNum !== null ? formatNumber(r.readbackNum) : ''}</td>
                <td>{formatTime(r.finishedAt, false)}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </div>
  );
}
