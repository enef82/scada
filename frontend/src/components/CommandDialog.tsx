import { useState } from 'react';
import { create } from 'zustand';
import { api, errorText } from '../lib/api';
import { formatValue, qualityCategory, qualityName } from '../lib/format';
import { useAcquisitionAlive, useTagValue } from '../lib/realtime';
import { toast, useCan } from '../lib/stores';
import type { Tag } from '../lib/types';
import { Modal, NumberInput } from './ui';

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
}

/** Отправка команды. Без подтверждения — только если тег явно это разрешает (write_confirm=false). */
export async function sendCommand(tag: Tag, value: number | string, confirmed: boolean): Promise<CommandResult> {
  const body = tag.dataType === 'string'
    ? { tagId: tag.id, valueStr: String(value), confirmed }
    : { tagId: tag.id, value: Number(value), confirmed };
  return api.post<CommandResult>('/api/commands', body);
}

export function requestCommand(r: Request) {
  if (!r.tag.writeConfirm && r.value !== undefined) {
    void sendCommand(r.tag, r.value, false)
      .then(report)
      .catch((e) => toast.error(`${r.tag.name}: ${errorText(e)}`));
    return;
  }
  useCmd.getState().open(r);
}

function report(r: CommandResult) {
  if (r.status === 'done') toast.ok(`Команда выполнена: ${r.result ?? ''}`);
  else toast.error(`Команда: ${r.status}${r.result ? ` — ${r.result}` : ''}`);
}

export function CommandDialog() {
  const req = useCmd((s) => s.req);
  const close = useCmd((s) => s.close);
  if (!req) return null;
  return <CommandDialogInner key={req.tag.id} req={req} onClose={close} />;
}

function CommandDialogInner({ req, onClose }: { req: Request; onClose: () => void }) {
  const { tag } = req;
  const v = useTagValue(tag.id);
  const can = useCan('operator');
  const alive = useAcquisitionAlive();
  const [num, setNum] = useState<number | null>(typeof req.value === 'number' ? req.value : v?.[1] ?? null);
  const [str, setStr] = useState<string>(typeof req.value === 'string' ? req.value : v?.[2] ?? '');
  const [busy, setBusy] = useState(false);
  const fixed = req.value !== undefined;

  const value = tag.dataType === 'string' ? str : num;
  const outOfRange = typeof value === 'number' &&
    ((tag.writeMin !== null && value < tag.writeMin) || (tag.writeMax !== null && value > tag.writeMax));
  const linkBad = qualityCategory(v?.[3]) === 'bad';

  const submit = async () => {
    if (value === null || value === undefined) return;
    setBusy(true);
    try {
      report(await sendCommand(tag, value, true));
      onClose();
    } catch (e) {
      toast.error(errorText(e));
    } finally {
      setBusy(false);
    }
  };

  return (
    <Modal title={req.label ?? 'Команда управления'} onClose={onClose}
      footer={
        <>
          <button onClick={onClose}>Отмена</button>
          <button className="primary" disabled={!can || busy || outOfRange || value === null || !alive} onClick={submit}>
            {busy ? 'Выполняется…' : 'Подтвердить'}
          </button>
        </>
      }>
      <div className="cmd">
        <div className="cmd-row"><span>Тег</span><b>{tag.name}</b></div>
        <div className="cmd-row"><span>Описание</span><span>{tag.description || '—'}</span></div>
        <div className="cmd-row">
          <span>Текущее значение</span>
          <span className={`q-${qualityCategory(v?.[3])}`}>
            {formatValue(tag, v?.[1], v?.[2])} {tag.unit} <small className="muted">({qualityName(v?.[3])})</small>
          </span>
        </div>
        <div className="cmd-row">
          <span>Новое значение</span>
          {fixed ? (
            <b>{tag.dataType === 'bool' ? (Number(req.value) ? 'ВКЛ' : 'ОТКЛ') : String(req.value)} {tag.unit}</b>
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
        {outOfRange && <div className="alert alert-bad">Значение вне допустимого диапазона</div>}
        {linkBad && <div className="alert alert-warn">Нет достоверной связи с тегом — команда может быть отклонена</div>}
        {!alive && <div className="alert alert-bad">Служба сбора недоступна — команды не принимаются</div>}
        {!can && <div className="alert alert-warn">Недостаточно прав для управления</div>}
        <p className="muted small">Команда действует 15 с. Если связь с устройством отсутствует, она не будет исполнена позже.</p>
      </div>
    </Modal>
  );
}
