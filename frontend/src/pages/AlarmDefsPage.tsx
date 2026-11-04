import { useCallback, useEffect, useMemo, useState } from 'react';
import { Empty, Field, Modal, NumberInput, TagPicker, askConfirm } from '../components/ui';
import { api, errorText } from '../lib/api';
import { alarmKindNames, priorityNames } from '../lib/format';
import { toast, useCan, useMeta } from '../lib/stores';
import type { AlarmDef, AlarmKind } from '../lib/types';

const ANALOG: AlarmKind[] = ['hihi', 'hi', 'lo', 'lolo'];

export function AlarmDefsPage() {
  const tagById = useMeta((s) => s.tagById);
  const can = useCan('config.edit');
  const [defs, setDefs] = useState<AlarmDef[]>([]);
  const [q, setQ] = useState('');
  const [edit, setEdit] = useState<Partial<AlarmDef> | null>(null);

  const load = useCallback(async () => {
    try { setDefs(await api.get<AlarmDef[]>('/api/alarm-defs')); }
    catch (e) { toast.error(errorText(e)); }
  }, []);
  useEffect(() => { void load(); }, [load]);

  const list = useMemo(() => {
    const s = q.trim().toLowerCase();
    return defs.filter((d) => !s || d.message.toLowerCase().includes(s) || (tagById.get(d.tagId)?.name ?? '').toLowerCase().includes(s));
  }, [defs, q, tagById]);

  const remove = async (d: AlarmDef) => {
    if (!(await askConfirm('Удалить тревогу? Удаление вступит в силу после публикации, история событий сохранится.', { ok: 'Удалить' }))) return;
    try { await api.del(`/api/alarm-defs/${d.id}`); void load(); }
    catch (e) { toast.error(errorText(e)); }
  };

  return (
    <div className="page">
      <div className="toolbar">
        <h3>Уставки тревог ({defs.length})</h3>
        <input placeholder="поиск" value={q} onChange={(e) => setQ(e.target.value)} />
        <span className="grow" />
        <button className="primary" disabled={!can}
          onClick={() => setEdit({ tagId: 0, kind: 'hi', limitValue: 0, hysteresis: 0, delayMs: 0, priority: 2, message: '', enabled: true })}>
          Добавить
        </button>
      </div>
      {list.length === 0 ? <Empty>Тревоги не настроены</Empty> : (
        <table className="table">
          <thead><tr><th>Тег</th><th>Тип</th><th>Уставка</th><th>Гистерезис</th><th>Задержка, мс</th><th>Приоритет</th><th>Сообщение</th><th>Вкл.</th><th /></tr></thead>
          <tbody>
            {list.map((d) => {
              const t = tagById.get(d.tagId);
              return (
                <tr key={d.id} className={d.enabled ? '' : 'row-disabled'}>
                  <td><b>{t?.name ?? `#${d.tagId}`}</b><div className="muted small">{t?.description}</div></td>
                  <td>{alarmKindNames[d.kind]}</td>
                  <td>{ANALOG.includes(d.kind) ? `${d.limitValue} ${t?.unit ?? ''}` : ''}</td>
                  <td>{ANALOG.includes(d.kind) ? d.hysteresis : ''}</td>
                  <td>{d.delayMs}</td>
                  <td><span className={`prio prio-${d.priority}`}>{priorityNames[d.priority]}</span></td>
                  <td>{d.message}</td>
                  <td>{d.enabled ? 'да' : 'нет'}</td>
                  <td className="actions">
                    <button disabled={!can} onClick={() => setEdit(d)}>Изменить</button>
                    <button disabled={!can} onClick={() => void remove(d)}>×</button>
                  </td>
                </tr>
              );
            })}
          </tbody>
        </table>
      )}
      {edit && <AlarmForm def={edit} onClose={() => setEdit(null)} onSaved={() => { setEdit(null); void load(); }} />}
    </div>
  );
}

function AlarmForm({ def, onClose, onSaved }: { def: Partial<AlarmDef>; onClose: () => void; onSaved: () => void }) {
  const tagById = useMeta((s) => s.tagById);
  const tagByName = useMeta((s) => s.tagByName);
  const [d, setD] = useState(def);
  const set = <K extends keyof AlarmDef>(k: K, v: AlarmDef[K]) => setD((x) => ({ ...x, [k]: v }));
  const analog = ANALOG.includes(d.kind as AlarmKind);

  const save = async () => {
    if (!d.tagId) return toast.error('Выберите тег');
    if (!d.message?.trim()) return toast.error('Укажите сообщение');
    try {
      const body = { ...d, id: d.id ?? 0, limitValue: analog ? d.limitValue : null };
      if (d.id) await api.put(`/api/alarm-defs/${d.id}`, body); else await api.post('/api/alarm-defs', body);
      toast.ok('Сохранено в черновик. Уставка заработает после публикации.');
      onSaved();
    } catch (e) { toast.error(errorText(e)); }
  };

  return (
    <Modal title={d.id ? 'Тревога' : 'Новая тревога'} onClose={onClose}
      footer={<><button onClick={onClose}>Отмена</button><button className="primary" onClick={save}>Сохранить</button></>}>
      <div className="form">
        <Field label="Тег" wide>
          <TagPicker value={tagById.get(d.tagId ?? 0)?.name} onChange={(n) => set('tagId', tagByName.get(n)?.id ?? 0)} />
        </Field>
        <Field label="Тип">
          <select value={d.kind} onChange={(e) => set('kind', e.target.value as AlarmKind)}>
            {Object.entries(alarmKindNames).map(([k, v]) => <option key={k} value={k}>{v}</option>)}
          </select>
        </Field>
        <Field label="Приоритет">
          <select value={d.priority} onChange={(e) => set('priority', Number(e.target.value))}>
            {[1, 2, 3, 4].map((p) => <option key={p} value={p}>{p} — {priorityNames[p]}</option>)}
          </select>
        </Field>
        {analog && <Field label="Уставка"><NumberInput value={d.limitValue} onChange={(v) => set('limitValue', v ?? 0)} /></Field>}
        {analog && <Field label="Гистерезис" hint="снятие при возврате за уставку ± гистерезис"><NumberInput value={d.hysteresis} onChange={(v) => set('hysteresis', v ?? 0)} /></Field>}
        <Field label="Задержка срабатывания, мс"><NumberInput value={d.delayMs} onChange={(v) => set('delayMs', v ?? 0)} /></Field>
        <Field label="Включена"><input type="checkbox" checked={!!d.enabled} onChange={(e) => set('enabled', e.target.checked)} /></Field>
        <Field label="Сообщение оператору" wide><input value={d.message ?? ''} onChange={(e) => set('message', e.target.value)} /></Field>
      </div>
    </Modal>
  );
}
