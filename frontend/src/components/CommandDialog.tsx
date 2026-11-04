import { useEffect, useState } from 'react';
import { create } from 'zustand';
import { api, ApiError, errorText } from '../lib/api';
import { formatTime, formatValue, qualityCategory, qualityName } from '../lib/format';
import { onRealtime, useAcquisitionAlive, useTagValue } from '../lib/realtime';
import { areaPath, commandPerm, toast, useCan } from '../lib/stores';
import type { AwaitingCommand, CommandClass, Tag } from '../lib/types';
import { Badge, Field, Modal, NumberInput } from './ui';

interface Request {
  tag: Tag;
  /** Фиксированное значение (кнопки пуск/стоп). Если не задано — оператор вводит уставку. */
  value?: number | string;
  label?: string;
}

const useCmd = create<{ req: Request | null; open: (r: Request) => void; close: () => void }>((set) => ({
  req: null,
  open: (req) => set({ req }),
  close: () => set({ req: null }),
}));

interface CommandResult {
  id: number;
  status: string;
  result: string | null;
  confirmSeconds?: number;
}

export interface CommandSettings { ttlSeconds: number; confirmSeconds: number; criticalConfirmMode: 'second_user' | 'permit' }
let settings: Promise<CommandSettings> | null = null;
export const commandSettings = () =>
  (settings ??= api.get<CommandSettings>('/api/commands/settings').catch(() => ({ ttlSeconds: 15, confirmSeconds: 60, criticalConfirmMode: 'second_user' as const })));

const CLASS_TEXT: Record<CommandClass, string> = { normal: 'Обычная', important: 'Важная', critical: 'Критичная' };

interface Extra { password?: string; comment?: string; permitCode?: string }

export async function sendCommand(tag: Tag, value: number | string, confirmed: boolean, extra: Extra = {}): Promise<CommandResult> {
  const body = tag.dataType === 'string'
    ? { tagId: tag.id, valueStr: String(value), confirmed, ...extra }
    : { tagId: tag.id, value: Number(value), confirmed, ...extra };
  return api.post<CommandResult>('/api/commands', body);
}

/** Без диалога — только обычная команда на теге, где подтверждение явно отключено. */
export function requestCommand(r: Request) {
  if (r.tag.commandClass === 'normal' && !r.tag.writeConfirm && r.value !== undefined) {
    void sendCommand(r.tag, r.value, false)
      .then(report)
      .catch((e) => toast.error(`${r.tag.name}: ${errorText(e)}`));
    return;
  }
  useCmd.getState().open(r);
}

function report(r: CommandResult) {
  if (r.status === 'done') toast.ok(`Команда выполнена: ${r.result ?? ''}`);
  else toast.error(`Команда: ${statusText(r.status)}${r.result ? ` — ${r.result}` : ''}`);
}

const statusText = (s: string) =>
  ({ done: 'выполнена', failed: 'не выполнена', expired: 'истёк срок', rejected: 'отклонена', awaiting_confirm: 'ждёт подтверждения' }[s] ?? s);

export function CommandDialog() {
  const req = useCmd((s) => s.req);
  const close = useCmd((s) => s.close);
  if (!req) return null;
  return <CommandDialogInner key={req.tag.id} req={req} onClose={close} />;
}

function CommandDialogInner({ req, onClose }: { req: Request; onClose: () => void }) {
  const { tag } = req;
  const cls = tag.commandClass ?? 'normal';
  const v = useTagValue(tag.id);
  const can = useCan(commandPerm(tag), tag.effectiveAreaId);
  const alive = useAcquisitionAlive();
  const [num, setNum] = useState<number | null>(typeof req.value === 'number' ? req.value : v?.[1] ?? null);
  const [str, setStr] = useState<string>(typeof req.value === 'string' ? req.value : v?.[2] ?? '');
  const [password, setPassword] = useState('');
  const [comment, setComment] = useState('');
  const [permitCode, setPermitCode] = useState('');
  const [busy, setBusy] = useState(false);
  const [cfg, setCfg] = useState<CommandSettings | null>(null);
  const [waiting, setWaiting] = useState<{ id: number; until: number } | null>(null);
  const [now, setNow] = useState(Date.now());
  useEffect(() => { void commandSettings().then(setCfg); }, []);

  // ожидание второго подтверждающего: итог приходит событием command
  useEffect(() => {
    if (!waiting) return;
    const t = setInterval(() => setNow(Date.now()), 500);
    const off = onRealtime('command', (p) => {
      const r = p as { id: number; status: string; result: string | null };
      if (r.id !== waiting.id || !['done', 'failed', 'expired', 'rejected'].includes(r.status)) return;
      report({ id: r.id, status: r.status, result: r.result });
      onClose();
    });
    return () => { clearInterval(t); off(); };
  }, [waiting, onClose]);

  const fixed = req.value !== undefined;
  const value = tag.dataType === 'string' ? str : num;
  const outOfRange = typeof value === 'number' &&
    ((tag.writeMin !== null && value < tag.writeMin) || (tag.writeMax !== null && value > tag.writeMax));
  const linkBad = qualityCategory(v?.[3]) === 'bad';
  const permit = cls === 'critical' && cfg?.criticalConfirmMode === 'permit';
  const needSecret = cls !== 'normal';
  const ready = value !== null && value !== undefined && !outOfRange && alive && can
    && (!needSecret || (password && comment.trim())) && (!permit || permitCode.trim());

  const submit = async () => {
    if (value === null || value === undefined) return;
    setBusy(true);
    try {
      const r = await sendCommand(tag, value, true, needSecret ? { password, comment: comment.trim(), permitCode: permit ? permitCode.trim() : undefined } : {});
      if (r.status === 'awaiting_confirm') {
        setWaiting({ id: r.id, until: Date.now() + (r.confirmSeconds ?? cfg?.confirmSeconds ?? 60) * 1000 });
        setPassword('');
        return;
      }
      report(r);
      onClose();
    } catch (e) {
      toast.error(errorText(e));
      if (e instanceof ApiError && e.status === 403) setPassword('');
    } finally {
      setBusy(false);
    }
  };

  if (waiting) {
    const left = Math.max(0, Math.ceil((waiting.until - now) / 1000));
    return (
      <Modal title="Ожидание второго подтверждения" onClose={onClose}
        footer={<button onClick={onClose}>Свернуть</button>}>
        <div className="cmd">
          <div className="confirm-wait">
            <div className={`confirm-timer ${left <= 10 ? 'warn' : ''}`}>{left} с</div>
            <div>
              Критичная команда <b>{tag.name}</b> ← <b>{formatCmdValue(tag, value)}</b> отправлена на подтверждение.
              Второй пользователь с правом «Критичные команды» должен подтвердить её со своего рабочего места.
              <div className="muted small">После подтверждения команда исполняется в течение {cfg?.ttlSeconds ?? 15} с. Не подтверждённая вовремя команда не исполнится никогда.</div>
            </div>
          </div>
          {left === 0 && <div className="alert alert-warn">Срок подтверждения истёк — команда не будет исполнена.</div>}
        </div>
      </Modal>
    );
  }

  return (
    <Modal title={req.label ?? 'Команда управления'} onClose={onClose}
      footer={
        <>
          <button onClick={onClose}>Отмена</button>
          <button className={cls === 'critical' ? 'danger' : 'primary'} disabled={!ready || busy} onClick={submit}>
            {busy ? 'Выполняется…' : cls === 'critical' && !permit ? 'Отправить на подтверждение' : 'Подтвердить'}
          </button>
        </>
      }>
      <div className="cmd">
        {cls !== 'normal' && (
          <div className={`alert ${cls === 'critical' ? 'alert-bad' : 'alert-warn'}`}>
            <b>{CLASS_TEXT[cls]} команда.</b>{' '}
            {cls === 'important' ? 'Повторно введите пароль и укажите причину.'
              : permit ? 'Повторно введите пароль, укажите причину и номер наряда-допуска.'
                : `Повторно введите пароль и укажите причину. Затем команду должен подтвердить второй пользователь за ${cfg?.confirmSeconds ?? 60} с.`}
          </div>
        )}
        <div className="cmd-row"><span>Тег</span><b>{tag.name}</b></div>
        <div className="cmd-row"><span>Описание</span><span>{tag.description || '—'}</span></div>
        <div className="cmd-row"><span>Область</span><span>{areaPath(tag.effectiveAreaId)}</span></div>
        <div className="cmd-row">
          <span>Текущее значение</span>
          <span className={`q-${qualityCategory(v?.[3])}`}>
            {formatValue(tag, v?.[1], v?.[2])} {tag.unit} <small className="muted">({qualityName(v?.[3])})</small>
          </span>
        </div>
        <div className="cmd-row">
          <span>Новое значение</span>
          {fixed ? (
            <b>{formatCmdValue(tag, req.value)}</b>
          ) : tag.dataType === 'string' ? (
            <input autoFocus value={str} onChange={(e) => setStr(e.target.value)} />
          ) : tag.dataType === 'bool' ? (
            <select value={num ?? 0} onChange={(e) => setNum(Number(e.target.value))}>
              <option value={1}>ВКЛ (1)</option>
              <option value={0}>ОТКЛ (0)</option>
            </select>
          ) : (
            <span className="row"><NumberInput value={num} onChange={setNum} allowEmpty /> {tag.unit}</span>
          )}
        </div>
        {(tag.writeMin !== null || tag.writeMax !== null) && (
          <div className="cmd-row"><span>Допустимо</span><span>{tag.writeMin ?? '−∞'} … {tag.writeMax ?? '+∞'}</span></div>
        )}
        {needSecret && (
          <div className="form cmd-secret">
            <Field label="Причина" wide><input id="cmd-comment" value={comment} onChange={(e) => setComment(e.target.value)} placeholder="наряд, заявка или кратко — зачем" /></Field>
            {permit && <Field label="Номер наряда-допуска"><input id="cmd-permit" value={permitCode} onChange={(e) => setPermitCode(e.target.value)} /></Field>}
            <Field label="Ваш пароль"><input id="cmd-password" type="password" autoComplete="current-password" value={password} onChange={(e) => setPassword(e.target.value)} /></Field>
          </div>
        )}
        {outOfRange && <div className="alert alert-bad">Значение вне допустимого диапазона</div>}
        {linkBad && <div className="alert alert-warn">Нет достоверной связи с тегом — команда может быть отклонена</div>}
        {!alive && <div className="alert alert-bad">Служба сбора недоступна — команды не принимаются</div>}
        {!can && <div className="alert alert-warn">Нет права «{PERM_TEXT[commandPerm(tag)]}» на область «{areaPath(tag.effectiveAreaId)}»</div>}
        <p className="muted small">Команда действует {cfg?.ttlSeconds ?? 15} с. Если связь с устройством отсутствует, она не будет исполнена позже.</p>
      </div>
    </Modal>
  );
}

const PERM_TEXT: Record<string, string> = { 'cmd.control': 'Команды', 'cmd.setpoint': 'Уставки', 'cmd.critical': 'Критичные команды' };

function formatCmdValue(tag: Tag, value: unknown) {
  if (tag.dataType === 'bool') return Number(value) ? 'ВКЛ' : 'ОТКЛ';
  return `${String(value ?? '—')} ${tag.unit}`;
}

// ------------------------------------------------------------------ окно второго подтверждения
/**
 * Запросы на подтверждение критичных команд от других пользователей.
 * Показывается тем, у кого есть право «Критичные команды» на область тега.
 */
export function ConfirmRequests() {
  const can = useCan('cmd.critical');
  const [list, setList] = useState<AwaitingCommand[]>([]);
  const [open, setOpen] = useState<AwaitingCommand | null>(null);
  const [now, setNow] = useState(Date.now());

  useEffect(() => {
    if (!can) return;
    const load = () => void api.get<AwaitingCommand[]>('/api/commands/awaiting').then(setList).catch(() => {});
    load();
    const offs = [onRealtime('confirmRequest', load), onRealtime('command', load), onRealtime('resync', load)];
    const poll = setInterval(load, 10_000);
    const tick = setInterval(() => setNow(Date.now()), 1000);
    return () => { offs.forEach((f) => f()); clearInterval(poll); clearInterval(tick); };
  }, [can]);

  const visible = list.filter((c) => Date.parse(c.expiresAt) > now);
  if (!can || visible.length === 0) return open ? <ConfirmDialog c={open} onClose={() => setOpen(null)} onDone={() => { setOpen(null); setList((l) => l.filter((x) => x.id !== open.id)); }} /> : null;
  return (
    <>
      <div className="confirm-requests" role="alert">
        {visible.map((c) => (
          <div key={c.id} className="confirm-request">
            <div>
              <Badge kind="bad">Нужно второе подтверждение</Badge>{' '}
              <b>{c.tagName}</b> ← <b>{c.valueStr ?? c.valueNum} {c.unit}</b>
              <div className="small">{c.requestedBy}, {formatTime(c.requestedAt, false)} · {c.areaPath}</div>
              {c.comment && <div className="small muted">«{c.comment}»</div>}
            </div>
            <div className="confirm-timer small">{Math.max(0, Math.ceil((Date.parse(c.expiresAt) - now) / 1000))} с</div>
            <button className="danger" onClick={() => setOpen(c)}>Рассмотреть</button>
          </div>
        ))}
      </div>
      {open && <ConfirmDialog c={open} onClose={() => setOpen(null)} onDone={() => { setOpen(null); setList((l) => l.filter((x) => x.id !== open.id)); }} />}
    </>
  );
}

function ConfirmDialog({ c, onClose, onDone }: { c: AwaitingCommand; onClose: () => void; onDone: () => void }) {
  const [password, setPassword] = useState('');
  const [comment, setComment] = useState('');
  const [busy, setBusy] = useState(false);
  const go = async (approve: boolean) => {
    setBusy(true);
    try {
      await api.post(`/api/commands/${c.id}/confirm`, { password, approve, comment: comment.trim() || undefined });
      toast[approve ? 'ok' : 'info'](approve ? `Команда ${c.tagName} подтверждена и отправлена на исполнение` : `Команда ${c.tagName} отклонена`);
      onDone();
    } catch (e) { toast.error(errorText(e)); } finally { setBusy(false); }
  };
  return (
    <Modal title="Подтверждение критичной команды" onClose={onClose}
      footer={<>
        <button onClick={onClose}>Закрыть</button>
        <button disabled={busy || !password} onClick={() => void go(false)}>Отклонить</button>
        <button className="danger" disabled={busy || !password} onClick={() => void go(true)}>Подтвердить исполнение</button>
      </>}>
      <div className="cmd">
        <div className="cmd-row"><span>Тег</span><b>{c.tagName}</b></div>
        <div className="cmd-row"><span>Описание</span><span>{c.tagDescription || '—'}</span></div>
        <div className="cmd-row"><span>Область</span><span>{c.areaPath}</span></div>
        <div className="cmd-row"><span>Сейчас</span><span>{c.currentValue ?? '—'} {c.unit}</span></div>
        <div className="cmd-row"><span>Станет</span><b>{c.valueStr ?? c.valueNum} {c.unit}</b></div>
        <div className="cmd-row"><span>Автор</span><span>{c.requestedBy}, {formatTime(c.requestedAt)}</span></div>
        <div className="cmd-row"><span>Причина</span><span>{c.comment || '—'}</span></div>
        <div className="form cmd-secret">
          <Field label="Комментарий (для отклонения)" wide><input value={comment} onChange={(e) => setComment(e.target.value)} /></Field>
          <Field label="Ваш пароль"><input id="confirm-password" type="password" value={password} onChange={(e) => setPassword(e.target.value)} /></Field>
        </div>
        <p className="muted small">Подтверждая, вы разделяете ответственность за команду. Защиты и блокировки остаются в ПЛК.</p>
      </div>
    </Modal>
  );
}
