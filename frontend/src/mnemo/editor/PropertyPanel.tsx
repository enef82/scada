import { Field, NumberInput, TagPicker } from '../../components/ui';
import { useMeta } from '../../lib/stores';
import { COMMON_FIELDS, ELEMENTS, type FieldDef, type MnemoDoc, type MnemoElement, type PropValue } from '../model';

interface Props {
  doc: MnemoDoc;
  selected: MnemoElement[];
  onDoc: (patch: Partial<MnemoDoc>) => void;
  onElement: (id: string, patch: Partial<MnemoElement>) => void;
  onProps: (ids: string[], key: string, value: PropValue) => void;
  onAlign: (mode: 'left' | 'right' | 'top' | 'bottom' | 'hcenter' | 'vcenter' | 'hdist' | 'vdist') => void;
}

export function PropertyPanel({ doc, selected, onDoc, onElement, onProps, onAlign }: Props) {
  if (selected.length === 0) {
    return (
      <div className="props">
        <h4>Схема</h4>
        <Field label="Ширина"><NumberInput value={doc.width} onChange={(v) => onDoc({ width: Math.max(200, v ?? 1600) })} /></Field>
        <Field label="Высота"><NumberInput value={doc.height} onChange={(v) => onDoc({ height: Math.max(200, v ?? 900) })} /></Field>
        <Field label="Фон"><ColorInput value={doc.background} onChange={(v) => onDoc({ background: v })} /></Field>
        <Field label="Шаг сетки"><NumberInput value={doc.grid} onChange={(v) => onDoc({ grid: Math.max(1, Math.min(100, v ?? 10)) })} /></Field>
        <p className="muted small">
          Перетащите элемент из палитры или щёлкните по нему. Выделение рамкой — протяжкой по пустому месту,
          добавление к выделению — Shift. Ctrl+Z/Y — отмена/повтор, Ctrl+D — копия, Del — удалить, стрелки — сдвиг.
        </p>
      </div>
    );
  }

  if (selected.length > 1) {
    const ids = selected.map((e) => e.id);
    const sameType = selected.every((e) => e.type === selected[0].type);
    return (
      <div className="props">
        <h4>Выделено: {selected.length}</h4>
        <div className="align-grid">
          <button onClick={() => onAlign('left')} title="По левому краю">⇤</button>
          <button onClick={() => onAlign('hcenter')} title="По центру (гориз.)">↔</button>
          <button onClick={() => onAlign('right')} title="По правому краю">⇥</button>
          <button onClick={() => onAlign('top')} title="По верхнему краю">⤒</button>
          <button onClick={() => onAlign('vcenter')} title="По центру (верт.)">↕</button>
          <button onClick={() => onAlign('bottom')} title="По нижнему краю">⤓</button>
          <button onClick={() => onAlign('hdist')} title="Распределить по горизонтали">⋯</button>
          <button onClick={() => onAlign('vdist')} title="Распределить по вертикали">⋮</button>
        </div>
        {sameType && (
          <>
            <h4>Общие свойства ({ELEMENTS[selected[0].type].label})</h4>
            {ELEMENTS[selected[0].type].fields.filter((f) => f.type !== 'tag').map((f) => (
              <PropField key={f.key} f={f} value={selected[0].props[f.key]} onChange={(v) => onProps(ids, f.key, v)} />
            ))}
          </>
        )}
      </div>
    );
  }

  const el = selected[0];
  const def = ELEMENTS[el.type];
  return (
    <div className="props">
      <h4>{def.label}</h4>
      <div className="props-geom">
        <Field label="X"><NumberInput value={el.x} onChange={(v) => onElement(el.id, { x: v ?? 0 })} /></Field>
        <Field label="Y"><NumberInput value={el.y} onChange={(v) => onElement(el.id, { y: v ?? 0 })} /></Field>
        <Field label={def.isLine ? 'ΔX' : 'Ширина'}><NumberInput value={el.w} onChange={(v) => onElement(el.id, { w: def.isLine ? v ?? 0 : Math.max(4, v ?? 4) })} /></Field>
        <Field label={def.isLine ? 'ΔY' : 'Высота'}><NumberInput value={el.h} onChange={(v) => onElement(el.id, { h: def.isLine ? v ?? 0 : Math.max(4, v ?? 4) })} /></Field>
        {!def.isLine && (
          <Field label="Поворот, °"><NumberInput value={el.rotation ?? 0} onChange={(v) => onElement(el.id, { rotation: v ?? 0 })} /></Field>
        )}
        <Field label="Закрепить">
          <input type="checkbox" checked={!!el.locked} onChange={(e) => onElement(el.id, { locked: e.target.checked })} />
        </Field>
      </div>
      {def.fields.map((f) => (
        <PropField key={f.key} f={f} value={el.props[f.key]} onChange={(v) => onProps([el.id], f.key, v)} />
      ))}
      <details>
        <summary>Дополнительно</summary>
        {COMMON_FIELDS.map((f) => (
          <PropField key={f.key} f={f} value={el.props[f.key]} onChange={(v) => onProps([el.id], f.key, v)} />
        ))}
      </details>
    </div>
  );
}

function PropField({ f, value, onChange }: { f: FieldDef; value: PropValue; onChange: (v: PropValue) => void }) {
  const mnemos = useMeta((s) => s.mnemos);
  switch (f.type) {
    case 'text':
      return <Field label={f.label} hint={f.hint}><input value={String(value ?? '')} onChange={(e) => onChange(e.target.value)} /></Field>;
    case 'number':
      return (
        <Field label={f.label} hint={f.hint}>
          <NumberInput value={value === '' || value === undefined ? null : Number(value)} allowEmpty
            onChange={(v) => onChange(v === null ? '' : v)} />
        </Field>
      );
    case 'color':
      return <Field label={f.label}><ColorInput value={String(value ?? '')} onChange={onChange} /></Field>;
    case 'bool':
      return (
        <Field label={f.label}>
          <input type="checkbox" checked={!!value} onChange={(e) => onChange(e.target.checked)} />
        </Field>
      );
    case 'select':
      return (
        <Field label={f.label}>
          <select value={String(value ?? '')} onChange={(e) => onChange(e.target.value)}>
            {f.options?.map((o) => <option key={o.value} value={o.value}>{o.label}</option>)}
          </select>
        </Field>
      );
    case 'tag':
      return (
        <Field label={f.label} hint={f.hint}>
          <TagPicker value={String(value ?? '')} onChange={onChange} filter={f.writable ? (t) => t.writable : undefined} />
        </Field>
      );
    case 'mnemo':
      return (
        <Field label={f.label}>
          <select value={String(value ?? '')} onChange={(e) => onChange(e.target.value ? Number(e.target.value) : '')}>
            <option value="">— не выбрана —</option>
            {mnemos.map((m) => <option key={m.id} value={m.id}>{m.name}</option>)}
          </select>
        </Field>
      );
  }
}

function ColorInput({ value, onChange }: { value: string; onChange: (v: string) => void }) {
  const isHex = /^#[0-9a-f]{6}$/i.test(value);
  return (
    <span className="row">
      <input type="color" value={isHex ? value : '#000000'} onChange={(e) => onChange(e.target.value)} />
      <input className="color-text" value={value} placeholder="нет" onChange={(e) => onChange(e.target.value)} />
    </span>
  );
}
