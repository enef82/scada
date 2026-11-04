import { useCallback, useEffect, useMemo, useState } from 'react';
import { Badge, Empty, Field, Modal } from '../components/ui';
import { plural } from '../components/Layout';
import { api, ApiError, errorText } from '../lib/api';
import { formatTime } from '../lib/format';
import { onRealtime, useRealtimeEvent } from '../lib/realtime';
import { toast, useDraft, useMeta } from '../lib/stores';
import type { ChangeItem, ChangeSummary, ConfigVersion, DraftDiff, ObjectType, ValidationIssue } from '../lib/types';

const TYPE_NAMES: Record<ObjectType, [string, string, string]> = {
  device: ['устройство', 'устройства', 'устройств'],
  tag: ['тег', 'тега', 'тегов'],
  alarm: ['тревога', 'тревоги', 'тревог'],
  mnemo: ['схема', 'схемы', 'схем'],
};
const TYPE_LABEL: Record<ObjectType, string> = { device: 'Устройство', tag: 'Тег', alarm: 'Тревога', mnemo: 'Схема' };
const GROUP_LABEL: Record<ObjectType, string> = { device: 'Устройства', tag: 'Теги', alarm: 'Тревоги', mnemo: 'Мнемосхемы' };
// verb: [мн. ч., м. р., ж. р.]
const OP: Record<ChangeItem['op'], { label: string; labelF: string; kind: 'ok' | 'info' | 'bad'; verb: [string, string, string] }> = {
  created: { label: 'Добавлен', labelF: 'Добавлена', kind: 'ok', verb: ['добавлено', 'добавлен', 'добавлена'] },
  updated: { label: 'Изменён', labelF: 'Изменена', kind: 'info', verb: ['изменено', 'изменён', 'изменена'] },
  deleted: { label: 'Удалён', labelF: 'Удалена', kind: 'bad', verb: ['удалено', 'удалён', 'удалена'] },
};
const fem = (t: ObjectType) => t === 'alarm' || t === 'mnemo';
const opLabel = (c: ChangeItem) => (fem(c.objectType) ? OP[c.op].labelF : OP[c.op].label);

const FIELD: Record<string, string> = {
  name: 'имя', description: 'описание', deviceId: 'устройство', address: 'адрес', dataType: 'тип', unit: 'ед. изм.',
  rawLow: 'сырое мин.', rawHigh: 'сырое макс.', engLow: 'инж. мин.', engHigh: 'инж. макс.', pollMs: 'опрос, мс',
  staleAfterMs: 'устаревание, мс', deadbandUi: 'зона нечувств. (экран)', deadbandArchive: 'зона нечувств. (архив)',
  archiveMode: 'архивирование', archiveIntervalS: 'интервал архива, с', writable: 'запись', writeMin: 'мин. записи',
  writeMax: 'макс. записи', writeConfirm: 'подтверждение', enabled: 'включён', protocol: 'протокол', endpoint: 'адрес устройства',
  settingsJson: 'параметры протокола', timeoutMs: 'тайм-аут, мс', tagId: 'тег', kind: 'тип тревоги', limit: 'уставка', limitValue: 'уставка', settings: 'параметры протокола',
  hysteresis: 'гистерезис', delayMs: 'задержка, мс', priority: 'приоритет', message: 'сообщение', parentId: 'раздел',
  sortOrder: 'порядок', version: 'содержимое',
};

/** «добавлено 12 тегов, изменён 1 тег, удалена 1 тревога» */
export function summaryText(s: ChangeSummary): string {
  const parts: string[] = [];
  for (const op of ['created', 'updated', 'deleted'] as const)
    for (const t of ['device', 'tag', 'alarm', 'mnemo'] as const) {
      const n = s[t]?.[op];
      if (!n) continue;
      const [one, few, many] = TYPE_NAMES[t];
      const verb = n === 1 ? OP[op].verb[fem(t) ? 2 : 1] : OP[op].verb[0];
      parts.push(`${verb} ${n} ${plural(n, one, few, many)}`);
    }
  return parts.join(', ');
}

function useValueFormatter() {
  const devices = useMeta((s) => s.devices);
  const tagById = useMeta((s) => s.tagById);
  return (field: string, v: unknown): string => {
    if (v === null || v === undefined || v === '') return '—';
    if (typeof v === 'boolean') return v ? 'да' : 'нет';
    if (typeof v === 'object') return JSON.stringify(v);
    if (field === 'deviceId') return devices.find((d) => d.id === v)?.name ?? `#${v}`;
    if (field === 'tagId') return tagById.get(Number(v))?.name ?? `#${v}`;
    if (field === 'version') return `в.${v}`;
    return String(v);
  };
}

export function PublishPage() {
  const [tab, setTab] = useState<'changes' | 'history'>('changes');
  const status = useDraft((s) => s.status);
  return (
    <div className="page">
      <div className="toolbar">
        <h3>Публикация конфигурации</h3>
        {status && (
          <span className="muted small">
            На объекте работает версия {status.publishedVersion} от {formatTime(status.publishedAt)}, {status.publishedBy}
            {status.appliedAt ? '' : ' · служба сбора ещё не подтвердила'}
          </span>
        )}
      </div>
      <div className="tabs">
        <button className={tab === 'changes' ? 'active' : ''} onClick={() => setTab('changes')}>
          Изменения в черновике{status?.draftChanges ? ` (${status.draftChanges})` : ''}
        </button>
        <button className={tab === 'history' ? 'active' : ''} onClick={() => setTab('history')}>История версий</button>
      </div>
      {tab === 'changes' ? <DraftTab /> : <HistoryTab />}
    </div>
  );
}

// ------------------------------------------------------------------ черновик
function DraftTab() {
  const [diff, setDiff] = useState<DraftDiff | null>(null);
  const [loading, setLoading] = useState(false);
  const [comment, setComment] = useState('');
  const [password, setPassword] = useState('');
  const [accepted, setAccepted] = useState(false);
  const [busy, setBusy] = useState(false);
  const [applied, setApplied] = useState<{ version: number; ms: number | null; timedOut: boolean } | null>(null);
  const [discard, setDiscard] = useState(false);
  const fmt = useValueFormatter();
  const reloadStatus = useDraft((s) => s.reload);

  const load = useCallback(async () => {
    setLoading(true);
    try { setDiff(await api.get<DraftDiff>('/api/draft/diff')); }
    catch (e) { toast.error(errorText(e)); }
    finally { setLoading(false); }
  }, []);
  useEffect(() => { void load(); }, [load]);
  // правки других инженеров сразу видны в списке
  useRealtimeEvent('config', useCallback(() => void load(), [load]));

  // ожидание подтверждения от службы сбора
  useEffect(() => {
    if (!applied || applied.ms !== null || applied.timedOut) return;
    const off = onRealtime('configApplied', (p) => {
      const v = p as { version: number; ms: number };
      if (v.version === applied.version) setApplied({ ...applied, ms: v.ms ?? 0 });
    });
    const t = setTimeout(() => setApplied((a) => (a && a.ms === null ? { ...a, timedOut: true } : a)), 30_000);
    return () => { off(); clearTimeout(t); };
  }, [applied]);

  const publish = async () => {
    if (!diff) return;
    setBusy(true);
    try {
      const r = await api.post<{ version: number }>('/api/publish', { baseVersion: diff.baseVersion, comment, password, acceptWarnings: accepted });
      setApplied({ version: r.version, ms: null, timedOut: false });
      setComment(''); setPassword(''); setAccepted(false);
      toast.ok(`Опубликована версия ${r.version}`);
      void reloadStatus();
      void load();
    } catch (e) {
      // 422 — ошибки проверок, 428 — нужно подтвердить предупреждения: показываем актуальный список
      if (e instanceof ApiError && (e.status === 422 || e.status === 428)) {
        const d = e.details as { errors: ValidationIssue[]; warnings: ValidationIssue[] };
        setDiff((x) => (x ? { ...x, errors: d.errors, warnings: d.warnings } : x));
      }
      if (e instanceof ApiError && e.status === 409) void load();
      toast.error(errorText(e));
    } finally {
      setBusy(false);
    }
  };

  const groups = useMemo(() => {
    const g = new Map<ObjectType, DraftDiff['changes']>();
    for (const c of diff?.changes ?? []) g.set(c.change.objectType, [...(g.get(c.change.objectType) ?? []), c]);
    return [...g.entries()];
  }, [diff]);

  if (!diff) return <Empty>{loading ? 'Сравнение с опубликованной версией…' : 'Нет данных'}</Empty>;
  const hasChanges = diff.changes.length > 0;
  const blocked = diff.errors.length > 0;
  const needAccept = diff.warnings.length > 0 && !accepted;

  return (
    <div className="publish">
      {applied && <AppliedBanner {...applied} onClose={() => setApplied(null)} />}

      {!hasChanges ? (
        <Empty>Черновик совпадает с работающей версией {diff.baseVersion}. Правки устройств, тегов, тревог и схем появятся здесь.</Empty>
      ) : (
        <>
          <p className="publish-lead">Относительно версии {diff.baseVersion}: {summaryText(diff.summary)}.</p>

          {(diff.errors.length > 0 || diff.warnings.length > 0) && (
            <div className="checks">
              {diff.errors.length > 0 && (
                <div className="alert alert-bad">
                  <b>Ошибки — публикация невозможна ({diff.errors.length})</b>
                  <ul>{diff.errors.map((i, k) => <li key={k}>{i.message}</li>)}</ul>
                </div>
              )}
              {diff.warnings.length > 0 && (
                <div className="alert alert-warn">
                  <b>Предупреждения ({diff.warnings.length})</b>
                  <ul>{diff.warnings.map((i, k) => <li key={k}>{i.message}</li>)}</ul>
                </div>
              )}
            </div>
          )}
          {diff.errors.length === 0 && diff.warnings.length === 0 && <div className="alert alert-ok">Проверки пройдены, замечаний нет</div>}

          {groups.map(([type, items]) => (
            <section key={type} className="change-group">
              <h4>{GROUP_LABEL[type]} · {items.length}</h4>
              <table className="table">
                <thead><tr><th style={{ width: 110 }}>Действие</th><th style={{ width: 220 }}>Объект</th><th>Что изменилось</th><th style={{ width: 160 }}>Кто</th></tr></thead>
                <tbody>
                  {items.map(({ change: c, authors, lastAt }) => (
                    <tr key={`${c.objectType}-${c.id}`} className={c.highlight ? 'row-highlight' : ''}>
                      <td><Badge kind={OP[c.op].kind}>{opLabel(c)}</Badge></td>
                      <td><b>{c.name}</b></td>
                      <td>
                        {c.fields.length === 0 ? <span className="muted">{c.op === 'created' ? 'новый объект' : 'объект удаляется'}</span> : (
                          <div className="field-changes">
                            {c.fields.map((f) => (
                              <div key={f.field}>
                                <span className="muted">{FIELD[f.field] ?? f.field}:</span>{' '}
                                <s className="old">{fmt(f.field, f.old)}</s> → <b>{fmt(f.field, f.new)}</b>
                              </div>
                            ))}
                          </div>
                        )}
                      </td>
                      <td className="small">{authors.join(', ') || <span className="muted">—</span>}{lastAt && <div className="muted">{formatTime(lastAt)}</div>}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </section>
          ))}

          <div className="publish-form">
            <Field label="Причина изменения" hint="номер заявки, наряда или кратко — что и зачем" wide>
              <input id="pub-comment" value={comment} onChange={(e) => setComment(e.target.value)} placeholder="Заявка 1452: добавлены теги КНС-3" />
            </Field>
            <Field label="Ваш пароль">
              <input id="pub-password" type="password" value={password} onChange={(e) => setPassword(e.target.value)} autoComplete="current-password" />
            </Field>
            {diff.warnings.length > 0 && (
              <label className="row small accept">
                <input type="checkbox" checked={accepted} onChange={(e) => setAccepted(e.target.checked)} />
                Ознакомлен с предупреждениями ({diff.warnings.length})
              </label>
            )}
            <div className="row">
              <button className="primary" disabled={busy || blocked || needAccept || !comment.trim() || !password} onClick={() => void publish()}>
                {busy ? 'Публикация…' : `Опубликовать версию ${diff.baseVersion + 1}`}
              </button>
              <button onClick={() => void load()} disabled={loading}>Проверить заново</button>
              <span className="grow" />
              <button className="danger-outline" onClick={() => setDiscard(true)}>Сбросить черновик</button>
            </div>
            {blocked && <p className="small warn">Исправьте ошибки — кнопка станет доступна.</p>}
          </div>
        </>
      )}
      {discard && <DiscardDialog count={diff.changes.length} onClose={() => setDiscard(false)} onDone={() => { setDiscard(false); void load(); void reloadStatus(); }} />}
    </div>
  );
}

function AppliedBanner({ version, ms, timedOut, onClose }: { version: number; ms: number | null; timedOut: boolean; onClose: () => void }) {
  return (
    <div className={`alert ${timedOut ? 'alert-warn' : ms !== null ? 'alert-ok' : 'alert-info'} applied`}>
      {ms !== null
        ? <>Версия {version} применена службой сбора за {(ms / 1000).toLocaleString('ru-RU', { maximumFractionDigits: 1 })} с. Операторы уже работают по ней.</>
        : timedOut
          ? <>Версия {version} опубликована, но служба сбора не подтвердила применение за 30 с. Проверьте раздел «Диагностика».</>
          : <>Версия {version} опубликована. Ожидание службы сбора…</>}
      <button className="icon-btn" onClick={onClose} aria-label="Скрыть">×</button>
    </div>
  );
}

function DiscardDialog({ count, onClose, onDone }: { count: number; onClose: () => void; onDone: () => void }) {
  const [password, setPassword] = useState('');
  const go = async () => {
    try {
      const r = await api.post<{ discarded: number }>('/api/draft/discard', { password });
      toast.ok(`Черновик сброшен, отменено изменений: ${r.discarded}`);
      onDone();
    } catch (e) { toast.error(errorText(e)); }
  };
  return (
    <Modal title="Сбросить черновик" onClose={onClose}
      footer={<><button onClick={onClose}>Отмена</button><button className="danger" disabled={!password} onClick={() => void go()}>Сбросить</button></>}>
      <p>Все неопубликованные правки ({count}) всех инженеров будут отменены, черновик вернётся к работающей версии. Действие попадёт в аудит.</p>
      <Field label="Ваш пароль"><input id="discard-password" type="password" value={password} onChange={(e) => setPassword(e.target.value)} /></Field>
    </Modal>
  );
}

// ------------------------------------------------------------------ история
function HistoryTab() {
  const [rows, setRows] = useState<ConfigVersion[]>([]);
  const [open, setOpen] = useState<number | null>(null);
  const [rollback, setRollback] = useState<ConfigVersion | null>(null);
  const load = useCallback(async () => {
    try { setRows(await api.get<ConfigVersion[]>('/api/versions')); } catch (e) { toast.error(errorText(e)); }
  }, []);
  useEffect(() => { void load(); }, [load]);
  useRealtimeEvent('published', useCallback(() => void load(), [load]));
  useRealtimeEvent('configApplied', useCallback(() => void load(), [load]));
  const current = rows[0]?.version;

  if (!rows.length) return <Empty>Версий пока нет</Empty>;
  return (
    <>
      <table className="table">
        <thead><tr><th>Версия</th><th>Опубликована</th><th>Кем</th><th>Причина</th><th>Изменения</th><th>Применена</th><th /></tr></thead>
        <tbody>
          {rows.map((v) => (
            <VersionRow key={v.version} v={v} current={v.version === current} open={open === v.version}
              onToggle={() => setOpen(open === v.version ? null : v.version)} onRollback={() => setRollback(v)} />
          ))}
        </tbody>
      </table>
      {rollback && <RollbackDialog v={rollback} onClose={() => setRollback(null)} onDone={() => { setRollback(null); void load(); }} />}
    </>
  );
}

function VersionRow({ v, current, open, onToggle, onRollback }: { v: ConfigVersion; current: boolean; open: boolean; onToggle: () => void; onRollback: () => void }) {
  const [details, setDetails] = useState<ChangeItem[] | null>(null);
  const fmt = useValueFormatter();
  useEffect(() => {
    if (open && !details) void api.get<{ changes: ChangeItem[] }>(`/api/versions/${v.version}`).then((r) => setDetails(r.changes)).catch(() => {});
  }, [open, details, v.version]);
  return (
    <>
      <tr className={current ? 'row-current' : ''}>
        <td><b>{v.version}</b>{current && <Badge kind="ok">работает</Badge>}{v.rollbackOf && <Badge kind="warn">откат к {v.rollbackOf}</Badge>}</td>
        <td>{formatTime(v.createdAt)}</td>
        <td>{v.createdBy}</td>
        <td>{v.comment}{v.warnings > 0 && <div className="muted small">принято предупреждений: {v.warnings}</div>}</td>
        <td className="small">{summaryText(v.summary) || <span className="muted">исходная конфигурация</span>}</td>
        <td className="small">{v.appliedAt ? `${formatTime(v.appliedAt, false)}${v.appliedMs !== null ? `, ${(v.appliedMs / 1000).toFixed(1)} с` : ''}` : <span className="warn">нет подтверждения</span>}</td>
        <td className="actions">
          <button onClick={onToggle}>{open ? 'Скрыть' : 'Подробно'}</button>
          {!current && <button onClick={onRollback}>Откатить к этой</button>}
        </td>
      </tr>
      {open && (
        <tr className="row-details">
          <td colSpan={7}>
            {!details ? 'Загрузка…' : details.length === 0 ? <span className="muted">Изменений нет</span> : (
              <ul className="version-changes">
                {details.map((c) => (
                  <li key={`${c.objectType}-${c.id}`}>
                    <Badge kind={OP[c.op].kind}>{opLabel(c)}</Badge>{' '}
                    {TYPE_LABEL[c.objectType]} <b>{c.name}</b>
                    {c.fields.length > 0 && <span className="muted"> — {c.fields.map((f) => `${FIELD[f.field] ?? f.field}: ${fmt(f.field, f.old)} → ${fmt(f.field, f.new)}`).join('; ')}</span>}
                  </li>
                ))}
              </ul>
            )}
          </td>
        </tr>
      )}
    </>
  );
}

function RollbackDialog({ v, onClose, onDone }: { v: ConfigVersion; onClose: () => void; onDone: () => void }) {
  const draftChanges = useDraft((s) => s.status?.draftChanges ?? 0);
  const [comment, setComment] = useState('');
  const [password, setPassword] = useState('');
  const [discardDraft, setDiscardDraft] = useState(false);
  const [warnings, setWarnings] = useState<ValidationIssue[] | null>(null);
  const [errors, setErrors] = useState<ValidationIssue[]>([]);
  const [accept, setAccept] = useState(false);
  const [busy, setBusy] = useState(false);
  const go = async () => {
    setBusy(true);
    try {
      const r = await api.post<{ version: number }>(`/api/versions/${v.version}/rollback`, { comment, password, discardDraft, acceptWarnings: accept });
      toast.ok(`Откат выполнен: опубликована версия ${r.version} с содержимым версии ${v.version}`);
      void useDraft.getState().reload();
      onDone();
    } catch (e) {
      if (e instanceof ApiError && (e.status === 422 || e.status === 428)) {
        const d = e.details as { errors: ValidationIssue[]; warnings: ValidationIssue[] };
        setErrors(d.errors); setWarnings(d.warnings);
      }
      toast.error(errorText(e));
    } finally { setBusy(false); }
  };
  return (
    <Modal title={`Откат к версии ${v.version}`} onClose={onClose}
      footer={<><button onClick={onClose}>Отмена</button>
        <button className="danger" disabled={busy || !comment.trim() || !password || (draftChanges > 0 && !discardDraft) || (warnings !== null && warnings.length > 0 && !accept) || errors.length > 0}
          onClick={() => void go()}>{busy ? 'Откат…' : 'Откатить'}</button></>}>
      <p>
        Конфигурация версии {v.version} от {formatTime(v.createdAt)} ({v.createdBy}) будет опубликована как новая версия.
        История не переписывается, к текущей версии можно будет вернуться так же.
      </p>
      {draftChanges > 0 && (
        <label className="row small accept">
          <input type="checkbox" checked={discardDraft} onChange={(e) => setDiscardDraft(e.target.checked)} />
          Сбросить неопубликованные правки черновика ({draftChanges})
        </label>
      )}
      {errors.length > 0 && <div className="alert alert-bad"><b>Ошибки</b><ul>{errors.map((i, k) => <li key={k}>{i.message}</li>)}</ul></div>}
      {warnings && warnings.length > 0 && (
        <div className="alert alert-warn">
          <b>Предупреждения</b><ul>{warnings.map((i, k) => <li key={k}>{i.message}</li>)}</ul>
          <label className="row small"><input type="checkbox" checked={accept} onChange={(e) => setAccept(e.target.checked)} /> Ознакомлен</label>
        </div>
      )}
      <div className="form">
        <Field label="Причина отката" wide><input id="rb-comment" value={comment} onChange={(e) => setComment(e.target.value)} /></Field>
        <Field label="Ваш пароль"><input id="rb-password" type="password" value={password} onChange={(e) => setPassword(e.target.value)} /></Field>
      </div>
    </Modal>
  );
}
