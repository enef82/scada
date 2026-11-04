import { useState } from 'react';
import { Badge, Empty, Field, Modal, NumberInput, askConfirm } from '../components/ui';
import { api, errorText } from '../lib/api';
import { formatNumber, formatTime } from '../lib/format';
import { toast, useCan, useMeta } from '../lib/stores';
import type { Device, Protocol } from '../lib/types';

const PROTOCOLS: { value: Protocol; label: string; endpoint: string; settings: string }[] = [
  { value: 'simulator', label: 'Симулятор', endpoint: '', settings: '{}' },
  { value: 'modbus_tcp', label: 'Modbus TCP', endpoint: '192.168.1.10:502', settings: '{"unitId": 1, "byteOrder": "ABCD", "maxRegisters": 100, "maxGap": 8}' },
  { value: 'opcua', label: 'OPC UA', endpoint: 'opc.tcp://192.168.1.20:4840', settings: '{"security": true, "autoAcceptUntrusted": false, "user": "", "password": ""}' },
];

const stateKind = (s?: string | null) => (s === 'online' ? 'ok' : s === 'error' ? 'bad' : s ? 'warn' : 'muted');
const stateName: Record<string, string> = { online: 'В работе', error: 'Нет связи', connecting: 'Подключение', disabled: 'Отключено' };

export function DevicesPage() {
  const devices = useMeta((s) => s.devices);
  const tags = useMeta((s) => s.tags);
  const reload = useMeta((s) => s.reloadDevices);
  const can = useCan('engineer');
  const [edit, setEdit] = useState<Partial<Device> | null>(null);

  const remove = async (d: Device) => {
    if (!(await askConfirm(`Удалить устройство «${d.name}»?\nОпрос прекратится после публикации.`, { ok: 'Удалить' }))) return;
    try { await api.del(`/api/devices/${d.id}`); toast.ok('Удаление записано в черновик'); void reload(); }
    catch (e) { toast.error(errorText(e)); }
  };

  return (
    <div className="page">
      <div className="toolbar">
        <h3>Устройства и каналы связи</h3>
        <span className="grow" />
        <button className="primary" disabled={!can}
          onClick={() => setEdit({ name: '', protocol: 'modbus_tcp', endpoint: PROTOCOLS[1].endpoint, settings: JSON.parse(PROTOCOLS[1].settings), enabled: true, pollMs: 1000, timeoutMs: 2000, description: '' })}>
          Добавить
        </button>
      </div>
      {devices.length === 0 ? <Empty>Устройств нет</Empty> : (
        <table className="table">
          <thead><tr><th>Имя</th><th>Протокол</th><th>Адрес</th><th>Опрос, мс</th><th>Тегов</th><th>Состояние</th><th>Последний обмен</th><th>Задержка</th><th>Ошибки</th><th /></tr></thead>
          <tbody>
            {devices.map((d) => (
              <tr key={d.id} className={d.enabled ? '' : 'row-disabled'}>
                <td><b>{d.name}</b><div className="muted small">{d.description}</div></td>
                <td>{PROTOCOLS.find((p) => p.value === d.protocol)?.label}</td>
                <td className="mono">{d.endpoint}</td>
                <td>{d.pollMs}</td>
                <td>{tags.filter((t) => t.deviceId === d.id).length}</td>
                <td>
                  <Badge kind={d.enabled ? stateKind(d.state) : 'muted'}>{d.enabled ? stateName[d.state ?? ''] ?? '—' : 'Отключено'}</Badge>
                  {d.lastError && d.state !== 'online' && <div className="muted small" title={d.lastError}>{d.lastError.slice(0, 60)}</div>}
                </td>
                <td>{formatTime(d.lastOkAt)}</td>
                <td>{d.avgLatencyMs !== null && d.avgLatencyMs !== undefined ? `${formatNumber(d.avgLatencyMs, 1)} мс` : ''}</td>
                <td>{d.errorCount ?? ''}</td>
                <td className="actions">
                  <button disabled={!can} onClick={() => setEdit(d)}>Изменить</button>
                  <button disabled={!can} onClick={() => void remove(d)}>Удалить</button>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
      {edit && <DeviceForm device={edit} onClose={() => setEdit(null)} onSaved={() => { setEdit(null); void reload(); }} />}
    </div>
  );
}

function DeviceForm({ device, onClose, onSaved }: { device: Partial<Device>; onClose: () => void; onSaved: () => void }) {
  const [d, setD] = useState(device);
  const [settings, setSettings] = useState(JSON.stringify(device.settings ?? {}, null, 2));
  const set = <K extends keyof Device>(k: K, v: Device[K]) => setD((x) => ({ ...x, [k]: v }));

  const save = async () => {
    let parsed: unknown;
    try { parsed = JSON.parse(settings || '{}'); }
    catch { return toast.error('Параметры протокола — неверный JSON'); }
    const body = { ...d, id: d.id ?? 0, settings: parsed };
    try {
      if (d.id) await api.put(`/api/devices/${d.id}`, body);
      else await api.post('/api/devices', body);
      toast.ok('Сохранено в черновик. Изменение заработает после публикации.');
      onSaved();
    } catch (e) { toast.error(errorText(e)); }
  };

  const proto = PROTOCOLS.find((p) => p.value === d.protocol);
  return (
    <Modal title={d.id ? `Устройство «${device.name}»` : 'Новое устройство'} onClose={onClose}
      footer={<><button onClick={onClose}>Отмена</button><button className="primary" onClick={save}>Сохранить</button></>}>
      <div className="form">
        <Field label="Имя"><input value={d.name ?? ''} onChange={(e) => set('name', e.target.value)} /></Field>
        <Field label="Протокол">
          <select value={d.protocol} onChange={(e) => {
            const p = PROTOCOLS.find((x) => x.value === e.target.value)!;
            set('protocol', p.value);
            if (!d.id) { set('endpoint', p.endpoint); setSettings(JSON.stringify(JSON.parse(p.settings), null, 2)); }
          }}>
            {PROTOCOLS.map((p) => <option key={p.value} value={p.value}>{p.label}</option>)}
          </select>
        </Field>
        <Field label="Адрес" hint={proto?.value === 'simulator' ? 'для симулятора не нужен ("fail" — имитация отказа)' : `например ${proto?.endpoint}`} wide>
          <input value={d.endpoint ?? ''} onChange={(e) => set('endpoint', e.target.value)} />
        </Field>
        <Field label="Период опроса, мс"><NumberInput value={d.pollMs} onChange={(v) => set('pollMs', v ?? 1000)} /></Field>
        <Field label="Тайм-аут, мс"><NumberInput value={d.timeoutMs} onChange={(v) => set('timeoutMs', v ?? 2000)} /></Field>
        <Field label="Включено"><input type="checkbox" checked={!!d.enabled} onChange={(e) => set('enabled', e.target.checked)} /></Field>
        <Field label="Описание" wide><input value={d.description ?? ''} onChange={(e) => set('description', e.target.value)} /></Field>
        <Field label="Параметры протокола (JSON)" wide hint={proto ? `шаблон: ${proto.settings}` : undefined}>
          <textarea rows={5} className="mono" value={settings} onChange={(e) => setSettings(e.target.value)} />
        </Field>
      </div>
    </Modal>
  );
}
