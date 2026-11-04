import { useCallback, useEffect, useState } from 'react';
import { useNavigate, useParams } from 'react-router-dom';
import { AreaSelect, Badge, Empty, Field, Modal, NumberInput, askConfirm } from '../components/ui';
import { useRealtimeEvent } from '../lib/realtime';
import { api, errorText } from '../lib/api';
import { formatTime } from '../lib/format';
import { areaPath, toast, useAreas, usePerms } from '../lib/stores';
import type { MnemoInfo } from '../lib/types';
import { Editor } from '../mnemo/editor/Editor';
import { emptyDoc, normalizeDoc, type MnemoDoc } from '../mnemo/model';

export function MnemosPage() {
  const { id } = useParams();
  return id ? <EditorLoader id={Number(id)} /> : <MnemoList />;
}

function MnemoList() {
  const perms = usePerms();
  useAreas((s) => s.byId);
  // редактор работает с черновиком: здесь и новые, ещё не опубликованные схемы
  const [mnemos, setMnemos] = useState<MnemoInfo[]>([]);
  const reload = useCallback(async () => {
    try { setMnemos(await api.get<MnemoInfo[]>('/api/mnemos?draft=1')); } catch (e) { toast.error(errorText(e)); }
  }, []);
  useEffect(() => { void reload(); }, [reload]);
  useRealtimeEvent('config', useCallback(() => void reload(), [reload]));
  useRealtimeEvent('published', useCallback(() => void reload(), [reload]));
  const navigate = useNavigate();
  const [edit, setEdit] = useState<Partial<MnemoInfo> | null>(null);

  const remove = async (m: MnemoInfo) => {
    if (!(await askConfirm(`Удалить схему «${m.name}»?\nОператоры перестанут её видеть после публикации. Версии схемы сохранятся для отката.`, { ok: 'Удалить' }))) return;
    try { await api.del(`/api/mnemos/${m.id}`); void reload(); }
    catch (e) { toast.error(errorText(e)); }
  };

  const parentName = (pid: number | null) => mnemos.find((m) => m.id === pid)?.name ?? '';

  return (
    <div className="page">
      <div className="toolbar">
        <h3>Мнемосхемы</h3>
        <span className="grow" />
        <button className="primary" onClick={() => setEdit({ name: '', parentId: null, sortOrder: 0 })}>Создать</button>
      </div>
      {mnemos.length === 0 ? <Empty>Схем нет — создайте первую</Empty> : (
        <table className="table">
          <thead><tr><th>Название</th><th>Область</th><th>Раздел</th><th>Порядок</th><th>Черновик</th><th>У операторов</th><th>Изменена</th><th>Кем</th><th /></tr></thead>
          <tbody>
            {mnemos.map((m) => (
              <tr key={m.id}>
                <td><b>{m.name}</b></td>
                <td className="small">{areaPath(m.areaId)}</td>
                <td>{parentName(m.parentId)}</td>
                <td>{m.sortOrder}</td>
                <td>в.{m.currentVersion}</td>
                <td>
                  {m.publishedVersion == null ? <Badge kind="warn">не опубликована</Badge>
                    : m.publishedVersion === m.currentVersion ? <span className="muted">в.{m.publishedVersion}</span>
                    : <Badge kind="info">в.{m.publishedVersion}, есть правки</Badge>}
                </td>
                <td>{formatTime(m.updatedAt)}</td>
                <td>{m.updatedBy}</td>
                <td className="actions">
                  <button className="primary" disabled={!perms('mnemo.edit', m.areaId)} onClick={() => navigate(`/eng/mnemos/${m.id}`)}>Редактор</button>
                  <button disabled={m.publishedVersion == null} onClick={() => navigate(`/mnemo/${m.id}`)}>Открыть</button>
                  <button disabled={!perms('mnemo.edit', m.areaId)} onClick={() => setEdit(m)}>Свойства</button>
                  <button disabled={!perms('mnemo.edit', m.areaId)} onClick={() => void remove(m)}>×</button>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
      {edit && <MnemoForm m={edit} all={mnemos} onClose={() => setEdit(null)} onSaved={(newId) => {
        setEdit(null);
        void reload();
        if (newId) navigate(`/eng/mnemos/${newId}`);
      }} />}
    </div>
  );
}

function MnemoForm({ m, all: mnemos, onClose, onSaved }: { m: Partial<MnemoInfo>; all: MnemoInfo[]; onClose: () => void; onSaved: (newId?: number) => void }) {
  const [name, setName] = useState(m.name ?? '');
  const [parentId, setParentId] = useState<number | null>(m.parentId ?? null);
  const [sortOrder, setSortOrder] = useState(m.sortOrder ?? 0);
  const [areaId, setAreaId] = useState<number | null>(m.areaId ?? null);
  const save = async () => {
    try {
      const body = { name, parentId, sortOrder, areaId };
      if (m.id) { await api.put(`/api/mnemos/${m.id}`, body); onSaved(); }
      else { const r = await api.post<{ id: number }>('/api/mnemos', body); onSaved(r.id); }
    } catch (e) { toast.error(errorText(e)); }
  };
  return (
    <Modal title={m.id ? 'Свойства схемы' : 'Новая схема'} onClose={onClose}
      footer={<><button onClick={onClose}>Отмена</button><button className="primary" disabled={!name.trim()} onClick={save}>Сохранить</button></>}>
      <div className="form">
        <Field label="Название" wide><input autoFocus value={name} onChange={(e) => setName(e.target.value)} /></Field>
        <Field label="Родительская схема (раздел дерева)">
          <select value={parentId ?? ''} onChange={(e) => setParentId(e.target.value ? Number(e.target.value) : null)}>
            <option value="">— верхний уровень —</option>
            {mnemos.filter((x) => x.id !== m.id).map((x) => <option key={x.id} value={x.id}>{x.name}</option>)}
          </select>
        </Field>
        <Field label="Порядок"><NumberInput value={sortOrder} onChange={(v) => setSortOrder(v ?? 0)} /></Field>
        <Field label="Область" hint="операторы видят схему при праве «Просмотр» на область" wide>
          <AreaSelect value={areaId} perm="mnemo.edit" inherit={m.id ? undefined : 'как у родительской схемы'} onChange={setAreaId} />
        </Field>
      </div>
    </Modal>
  );
}

function EditorLoader({ id }: { id: number }) {
  const navigate = useNavigate();
  const [state, setState] = useState<{ name: string; doc: MnemoDoc; version: number } | null>(null);
  const [error, setError] = useState('');
  useEffect(() => {
    api.get<{ name: string; version: number; doc: unknown }>(`/api/mnemos/${id}?draft=1`)
      .then((r) => setState({ name: r.name, version: r.version, doc: r.doc ? normalizeDoc(r.doc) : emptyDoc() }))
      .catch((e) => setError(errorText(e)));
  }, [id]);
  if (error) return <Empty>{error}</Empty>;
  if (!state) return <Empty>Загрузка…</Empty>;
  return <Editor mnemoId={id} name={state.name} initialDoc={state.doc} initialVersion={state.version}
    onClose={() => navigate('/eng/mnemos')} />;
}
