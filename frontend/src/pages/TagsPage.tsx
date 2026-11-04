import { useMemo, useRef, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { Empty, Field, Modal, NumberInput, QualityDot, askConfirm } from '../components/ui';
import { requestCommand } from '../components/CommandDialog';
import { api, ApiError, errorText } from '../lib/api';
import { formatTime, formatValue, qualityName } from '../lib/format';
import { useTagSubscription, useTagValue } from '../lib/realtime';
import { toast, useCan, useMeta } from '../lib/stores';
import type { ArchiveMode, DataType, Tag } from '../lib/types';

const TYPES: DataType[] = ['bool', 'int16', 'uint16', 'int32', 'uint32', 'float32', 'float64', 'string'];
const PAGE = 200;

const ADDRESS_HINT: Record<string, string> = {
  simulator: 'sine:period=60,amp=10,offset=50 · ramp:period=30,min=0,max=100 · random:min=0,max=1 · square:period=10 · memory:имя=0 · follow:имя,tau=5',
  modbus_tcp: 'hr:100 · ir:20:cdab · hr:5.3 (бит) · co:0 · di:7 · hr:200#10 (строка 10 рег.) — смещение с нуля',
  opcua: 'ns=2;s=Line1.Pump1.Speed · ns=3;i=1001',
};

export function TagsPage() {
  const tags = useMeta((s) => s.tags);
  const devices = useMeta((s) => s.devices);
  const reload = useMeta((s) => s.reloadTags);
  const can = useCan('engineer');
  const navigate = useNavigate();
  const [q, setQ] = useState('');
  const [deviceId, setDeviceId] = useState<number | ''>('');
  const [page, setPage] = useState(0);
  const [edit, setEdit] = useState<Partial<Tag> | null>(null);
  const [importErrors, setImportErrors] = useState<string[] | null>(null);
  const fileRef = useRef<HTMLInputElement>(null);

  const filtered = useMemo(() => {
    const s = q.trim().toLowerCase();
    return tags.filter((t) => (deviceId === '' || t.deviceId === deviceId)
      && (!s || t.name.toLowerCase().includes(s) || t.description.toLowerCase().includes(s) || t.address.toLowerCase().includes(s)));
  }, [tags, q, deviceId]);
  const visible = filtered.slice(page * PAGE, (page + 1) * PAGE);
  useTagSubscription('tags-page', visible.map((t) => t.id));

  const remove = async (t: Tag) => {
    if (!(await askConfirm(`Удалить тег «${t.name}»?\nУдаление попадёт в черновик и вступит в силу после публикации. Архив тега сохранится.`, { ok: 'Удалить' }))) return;
    try { await api.del(`/api/tags/${t.id}`); void reload(); }
    catch (e) { toast.error(errorText(e)); }
  };

  const importCsv = async (file: File) => {
    try {
      const r = await api.post<{ inserted: number; updated: number }>('/api/tags/import', await file.text());
      toast.ok(`Импорт в черновик: добавлено ${r.inserted}, обновлено ${r.updated}. Проверьте и опубликуйте.`);
      void reload();
    } catch (e) {
      const errs = e instanceof ApiError ? (e.details as { errors?: string[] })?.errors : undefined;
      if (errs?.length) setImportErrors(errs); else toast.error(errorText(e));
    }
  };

  const newTag = (): Partial<Tag> => ({
    name: '', description: '', deviceId: devices[0]?.id, address: '', dataType: 'float32', unit: '',
    rawLow: null, rawHigh: null, engLow: null, engHigh: null, pollMs: null, staleAfterMs: null,
    deadbandUi: 0, deadbandArchive: 0, archiveMode: 'change', archiveIntervalS: 60,
    writable: false, writeMin: null, writeMax: null, writeConfirm: true, enabled: true,
  });

  return (
    <div className="page">
      <div className="toolbar">
        <h3>Теги ({filtered.length})</h3>
        <input placeholder="поиск: имя, описание, адрес" value={q} onChange={(e) => { setQ(e.target.value); setPage(0); }} />
        <select value={deviceId} onChange={(e) => { setDeviceId(e.target.value ? Number(e.target.value) : ''); setPage(0); }}>
          <option value="">все устройства</option>
          {devices.map((d) => <option key={d.id} value={d.id}>{d.name}</option>)}
        </select>
        <span className="grow" />
        <button className="primary" disabled={!can || !devices.length} onClick={() => setEdit(newTag())}>Добавить</button>
        <button disabled={!can} onClick={() => fileRef.current?.click()}>Импорт CSV</button>
        <a className="btn" href="/api/tags/export">Экспорт CSV</a>
        <input ref={fileRef} type="file" accept=".csv,text/csv" hidden
          onChange={(e) => { const f = e.target.files?.[0]; if (f) void importCsv(f); e.target.value = ''; }} />
      </div>
      {filtered.length === 0 ? <Empty>Тегов нет</Empty> : (
        <table className="table">
          <thead><tr><th /><th>Имя</th><th>Описание</th><th>Устройство</th><th>Адрес</th><th>Тип</th><th>Значение</th><th>Время</th><th>Архив</th><th /></tr></thead>
          <tbody>
            {visible.map((t) => (
              <TagRow key={t.id} tag={t} device={devices.find((d) => d.id === t.deviceId)?.name ?? '?'} can={can}
                onEdit={() => setEdit(t)} onDelete={() => void remove(t)} onTrend={() => navigate(`/trends?tags=${t.id}`)} />
            ))}
          </tbody>
        </table>
      )}
      {filtered.length > PAGE && (
        <div className="toolbar">
          <button disabled={page === 0} onClick={() => setPage(page - 1)}>←</button>
          <span>стр. {page + 1} из {Math.ceil(filtered.length / PAGE)}</span>
          <button disabled={(page + 1) * PAGE >= filtered.length} onClick={() => setPage(page + 1)}>→</button>
        </div>
      )}
      {edit && <TagForm tag={edit} onClose={() => setEdit(null)} onSaved={() => { setEdit(null); void reload(); }} />}
      {importErrors && (
        <Modal title="Ошибки импорта — ничего не загружено" onClose={() => setImportErrors(null)} wide>
          <pre className="errors">{importErrors.join('\n')}</pre>
          <p className="muted small">Колонки (разделитель «;»): name; description; device; address; data_type; unit; raw_low; raw_high; eng_low; eng_high; poll_ms; stale_after_ms; deadband_ui; deadband_archive; archive_mode; archive_interval_s; writable; write_min; write_max; write_confirm; enabled. Обязательные — name, device, address, data_type. Проще всего взять за основу файл экспорта.</p>
        </Modal>
      )}
    </div>
  );
}

function TagRow(props: { tag: Tag; device: string; can: boolean; onEdit: () => void; onDelete: () => void; onTrend: () => void }) {
  const { tag: t } = props;
  const v = useTagValue(t.id);
  const canWrite = useCan('operator') && t.writable;
  return (
    <tr className={t.enabled ? '' : 'row-disabled'}>
      <td><QualityDot q={v?.[3]} /></td>
      <td><b>{t.name}</b></td>
      <td>{t.description}</td>
      <td>{props.device}</td>
      <td className="mono">{t.address}</td>
      <td>{t.dataType}{t.writable ? ' ✎' : ''}</td>
      <td className="mono" title={qualityName(v?.[3])}>{formatValue(t, v?.[1], v?.[2])} {t.unit}</td>
      <td className="small">{v ? formatTime(v[4], false) : ''}</td>
      <td className="small">{t.archiveMode === 'none' ? '—' : t.archiveMode === 'change' ? 'по изм.' : `${t.archiveIntervalS} с`}</td>
      <td className="actions">
        {canWrite && <button onClick={() => requestCommand({ tag: t })}>Запись</button>}
        <button onClick={props.onTrend}>Тренд</button>
        <button disabled={!props.can} onClick={props.onEdit}>Изменить</button>
        <button disabled={!props.can} onClick={props.onDelete}>×</button>
      </td>
    </tr>
  );
}

function TagForm({ tag, onClose, onSaved }: { tag: Partial<Tag>; onClose: () => void; onSaved: () => void }) {
  const devices = useMeta((s) => s.devices);
  const [t, setT] = useState(tag);
  const set = <K extends keyof Tag>(k: K, v: Tag[K]) => setT((x) => ({ ...x, [k]: v }));
  const protocol = devices.find((d) => d.id === t.deviceId)?.protocol ?? 'simulator';

  const save = async () => {
    try {
      const body = { ...t, id: t.id ?? 0 };
      if (t.id) await api.put(`/api/tags/${t.id}`, body); else await api.post('/api/tags', body);
      toast.ok('Тег сохранён в черновик. Опубликуйте, чтобы он заработал.');
      onSaved();
    } catch (e) { toast.error(errorText(e)); }
  };

  const nf = (k: keyof Tag, label: string, hint?: string) => (
    <Field label={label} hint={hint}>
      <NumberInput value={t[k] as number | null} allowEmpty onChange={(v) => set(k, v as never)} />
    </Field>
  );

  return (
    <Modal title={t.id ? `Тег ${tag.name}` : 'Новый тег'} onClose={onClose} wide
      footer={<><button onClick={onClose}>Отмена</button><button className="primary" onClick={save}>Сохранить</button></>}>
      <div className="form form-3">
        <Field label="Имя" hint="уникальное, латиница, например P101_Pressure"><input value={t.name ?? ''} onChange={(e) => set('name', e.target.value)} /></Field>
        <Field label="Описание" wide><input value={t.description ?? ''} onChange={(e) => set('description', e.target.value)} /></Field>
        <Field label="Устройство">
          <select value={t.deviceId} onChange={(e) => set('deviceId', Number(e.target.value))}>
            {devices.map((d) => <option key={d.id} value={d.id}>{d.name}</option>)}
          </select>
        </Field>
        <Field label="Адрес" hint={ADDRESS_HINT[protocol]} wide><input className="mono" value={t.address ?? ''} onChange={(e) => set('address', e.target.value)} /></Field>
        <Field label="Тип данных">
          <select value={t.dataType} onChange={(e) => set('dataType', e.target.value as DataType)}>
            {TYPES.map((x) => <option key={x} value={x}>{x}</option>)}
          </select>
        </Field>
        <Field label="Ед. изм."><input value={t.unit ?? ''} onChange={(e) => set('unit', e.target.value)} /></Field>
        <Field label="Включён"><input type="checkbox" checked={!!t.enabled} onChange={(e) => set('enabled', e.target.checked)} /></Field>

        <div className="form-section">Масштабирование (линейное; пусто — без масштаба)</div>
        {nf('rawLow', 'Сырое мин.')}{nf('rawHigh', 'Сырое макс.')}{nf('engLow', 'Инж. мин.')}{nf('engHigh', 'Инж. макс.')}

        <div className="form-section">Опрос и обработка</div>
        {nf('pollMs', 'Период опроса, мс', 'пусто — как у устройства')}
        {nf('staleAfterMs', 'Устаревание, мс', 'нет обновлений дольше — «устарело»')}
        {nf('deadbandUi', 'Зона нечувств. (экран)')}
        {nf('deadbandArchive', 'Зона нечувств. (архив)')}
        <Field label="Архивирование">
          <select value={t.archiveMode} onChange={(e) => set('archiveMode', e.target.value as ArchiveMode)}>
            <option value="change">по изменению</option>
            <option value="interval">по интервалу</option>
            <option value="none">не архивировать</option>
          </select>
        </Field>
        {nf('archiveIntervalS', t.archiveMode === 'interval' ? 'Интервал, с' : 'Принудительно не реже, с')}

        <div className="form-section">Запись из SCADA</div>
        <Field label="Разрешить запись"><input type="checkbox" checked={!!t.writable} onChange={(e) => set('writable', e.target.checked)} /></Field>
        {nf('writeMin', 'Мин. значение')}{nf('writeMax', 'Макс. значение')}
        <Field label="Подтверждение оператора"><input type="checkbox" checked={!!t.writeConfirm} onChange={(e) => set('writeConfirm', e.target.checked)} /></Field>
      </div>
    </Modal>
  );
}
