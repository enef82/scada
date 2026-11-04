import { create } from 'zustand';
import { useEffect, useMemo, useRef, useState, type ReactNode } from 'react';
import { useMeta, useToasts } from '../lib/stores';
import type { Tag } from '../lib/types';

export function Modal(props: {
  title: string;
  onClose: () => void;
  children: ReactNode;
  footer?: ReactNode;
  wide?: boolean;
}) {
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => e.key === 'Escape' && props.onClose();
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [props]);
  return (
    <div className="modal-backdrop" onMouseDown={(e) => e.target === e.currentTarget && props.onClose()}>
      <div className={`modal ${props.wide ? 'modal-wide' : ''}`} role="dialog" aria-label={props.title}>
        <div className="modal-head">
          <span>{props.title}</span>
          <button className="icon-btn" onClick={props.onClose} aria-label="Закрыть">×</button>
        </div>
        <div className="modal-body">{props.children}</div>
        {props.footer && <div className="modal-foot">{props.footer}</div>}
      </div>
    </div>
  );
}

export function Field(props: { label: string; children: ReactNode; hint?: string; wide?: boolean }) {
  return (
    <label className={`field ${props.wide ? 'field-wide' : ''}`}>
      <span className="field-label">{props.label}</span>
      {props.children}
      {props.hint && <span className="field-hint">{props.hint}</span>}
    </label>
  );
}

export function NumberInput(props: {
  value: number | null | undefined;
  onChange: (v: number | null) => void;
  allowEmpty?: boolean;
  step?: number;
  min?: number;
  max?: number;
}) {
  const [text, setText] = useState(props.value === null || props.value === undefined ? '' : String(props.value));
  useEffect(() => {
    const cur = text.trim() === '' ? null : Number(text.replace(',', '.'));
    if (cur !== (props.value ?? null)) setText(props.value === null || props.value === undefined ? '' : String(props.value));
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [props.value]);
  return (
    <input
      inputMode="decimal"
      value={text}
      step={props.step}
      onChange={(e) => {
        setText(e.target.value);
        const s = e.target.value.trim().replace(',', '.');
        if (s === '') props.onChange(props.allowEmpty ? null : 0);
        else if (!Number.isNaN(Number(s))) props.onChange(Number(s));
      }}
    />
  );
}

/** Выбор тега с поиском по имени и описанию. Значение — имя тега (стабильно при экспорте схем). */
export function TagPicker(props: {
  value: string | undefined;
  onChange: (name: string) => void;
  filter?: (t: Tag) => boolean;
  placeholder?: string;
}) {
  const tags = useMeta((s) => s.tags);
  const [open, setOpen] = useState(false);
  const [q, setQ] = useState('');
  const box = useRef<HTMLDivElement>(null);

  useEffect(() => {
    const close = (e: MouseEvent) => !box.current?.contains(e.target as Node) && setOpen(false);
    document.addEventListener('mousedown', close);
    return () => document.removeEventListener('mousedown', close);
  }, []);

  const list = useMemo(() => {
    const s = q.trim().toLowerCase();
    return tags
      .filter((t) => (props.filter ? props.filter(t) : true))
      .filter((t) => !s || t.name.toLowerCase().includes(s) || t.description.toLowerCase().includes(s))
      .slice(0, 200);
  }, [tags, q, props]);

  const current = tags.find((t) => t.name === props.value);
  return (
    <div className="tagpicker" ref={box}>
      <button type="button" className="tagpicker-btn" onClick={() => setOpen(!open)}>
        {props.value ? (
          <span>
            <b>{props.value}</b> {current ? <small>{current.description}</small> : <small className="warn">не найден</small>}
          </span>
        ) : (
          <span className="muted">{props.placeholder ?? 'выберите тег'}</span>
        )}
      </button>
      {open && (
        <div className="tagpicker-pop">
          <input autoFocus placeholder="поиск…" value={q} onChange={(e) => setQ(e.target.value)} />
          <div className="tagpicker-list">
            <div className="tagpicker-item muted" onClick={() => { props.onChange(''); setOpen(false); }}>— не привязан —</div>
            {list.map((t) => (
              <div key={t.id} className={`tagpicker-item ${t.name === props.value ? 'sel' : ''}`}
                onClick={() => { props.onChange(t.name); setOpen(false); }}>
                <b>{t.name}</b> <small>{t.description}</small> <small className="muted">{t.dataType}{t.writable ? ' · запись' : ''}</small>
              </div>
            ))}
          </div>
        </div>
      )}
    </div>
  );
}

export function Toasts() {
  const toasts = useToasts((s) => s.toasts);
  const remove = useToasts((s) => s.remove);
  return (
    <div className="toasts">
      {toasts.map((t) => (
        <div key={t.id} className={`toast toast-${t.kind}`} onClick={() => remove(t.id)}>
          {t.text}
        </div>
      ))}
    </div>
  );
}

export function Empty(props: { children: ReactNode }) {
  return <div className="empty">{props.children}</div>;
}

export function QualityDot(props: { q: number | undefined }) {
  const q = props.q;
  const cls = q === undefined ? 'bad' : q < 64 ? 'good' : q < 128 ? 'unc' : 'bad';
  return <span className={`qdot qdot-${cls}`} />;
}

export function Badge(props: { kind?: 'ok' | 'warn' | 'bad' | 'muted' | 'info'; children: ReactNode }) {
  return <span className={`badge badge-${props.kind ?? 'muted'}`}>{props.children}</span>;
}

// ------------------------------------------------------------------ подтверждение действия
// window.confirm не работает во встраиваемых окнах и выглядит чужеродно — свой диалог.
interface ConfirmReq { text: string; ok: string; danger: boolean; resolve: (v: boolean) => void }
const useConfirmStore = create<{ req: ConfirmReq | null }>(() => ({ req: null }));

export function askConfirm(text: string, opts: { ok?: string; danger?: boolean } = {}): Promise<boolean> {
  return new Promise((resolve) =>
    useConfirmStore.setState({ req: { text, ok: opts.ok ?? 'Да', danger: opts.danger ?? true, resolve } }));
}

export function ConfirmHost() {
  const req = useConfirmStore((s) => s.req);
  if (!req) return null;
  const close = (v: boolean) => { useConfirmStore.setState({ req: null }); req.resolve(v); };
  return (
    <Modal title="Подтверждение" onClose={() => close(false)}
      footer={<>
        <button onClick={() => close(false)}>Отмена</button>
        <button className={req.danger ? 'danger' : 'primary'} autoFocus onClick={() => close(true)}>{req.ok}</button>
      </>}>
      <p style={{ margin: 0, whiteSpace: 'pre-line' }}>{req.text}</p>
    </Modal>
  );
}
