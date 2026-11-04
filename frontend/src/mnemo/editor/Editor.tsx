import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { Modal, askConfirm } from '../../components/ui';
import { api, errorText } from '../../lib/api';
import { formatTime } from '../../lib/format';
import { useTagSubscription } from '../../lib/realtime';
import { toast, useMeta } from '../../lib/stores';
import { DEMO } from '../../demo/flag';
import { ElementView, SvgDefs, type RenderCtx } from '../elements';
import { MnemoView } from '../MnemoView';
import {
  boundTagNames, bounds, createElement, ELEMENTS, newId, normalizeDoc,
  type ElementType, type MnemoDoc, type MnemoElement, type PropValue,
} from '../model';
import { PropertyPanel } from './PropertyPanel';

type Handle = 'nw' | 'n' | 'ne' | 'e' | 'se' | 's' | 'sw' | 'w' | 'p1' | 'p2';

type Drag =
  | { kind: 'move'; start: Pt; orig: Map<string, Pt>; moved: boolean; before: MnemoDoc }
  | { kind: 'resize'; id: string; handle: Handle; start: Pt; orig: MnemoElement; before: MnemoDoc }
  | { kind: 'marquee'; start: Pt; cur: Pt; additive: boolean };

interface Pt { x: number; y: number }

const editCtx: RenderCtx = { editing: true, canOperate: false, navigate: () => {}, openFaceplate: () => {} };
const GROUPS = ['Графика', 'Данные', 'Управление', 'Оборудование', 'Навигация'] as const;
const DND_TYPE = 'application/x-scada-element';

interface Props {
  mnemoId: number;
  name: string;
  initialDoc: MnemoDoc;
  initialVersion: number;
  onClose: () => void;
}

export function Editor({ mnemoId, name, initialDoc, initialVersion, onClose }: Props) {
  const [doc, setDoc] = useState<MnemoDoc>(initialDoc);
  const [baseVersion, setBaseVersion] = useState(initialVersion);
  const [sel, setSel] = useState<Set<string>>(new Set());
  const [zoom, setZoom] = useState(1);
  const [snap, setSnap] = useState(true);
  const [preview, setPreview] = useState(false);
  const [dirty, setDirty] = useState(false);
  const [saving, setSaving] = useState(false);
  const [drag, setDrag] = useState<Drag | null>(null);
  const [showVersions, setShowVersions] = useState(false);
  const undo = useRef<MnemoDoc[]>([]);
  const redo = useRef<MnemoDoc[]>([]);
  const clipboard = useRef<MnemoElement[]>([]);
  const svgRef = useRef<SVGSVGElement>(null);
  const scrollRef = useRef<HTMLDivElement>(null);
  const fileRef = useRef<HTMLInputElement>(null);

  // живые значения в редакторе — для проверки привязок
  const tagByName = useMeta((s) => s.tagByName);
  const ids = useMemo(() => boundTagNames(doc).map((n) => tagByName.get(n)?.id).filter((x): x is number => !!x), [doc, tagByName]);
  useTagSubscription('editor', ids);

  // ---------------------------------------------------------------- изменение документа с историей
  const commit = useCallback((next: MnemoDoc, before?: MnemoDoc) => {
    setDoc((cur) => {
      undo.current.push(before ?? cur);
      if (undo.current.length > 200) undo.current.shift();
      redo.current = [];
      return next;
    });
    setDirty(true);
  }, []);

  const update = useCallback((fn: (d: MnemoDoc) => MnemoDoc) => {
    setDoc((cur) => {
      const next = fn(cur);
      if (next !== cur) {
        undo.current.push(cur);
        if (undo.current.length > 200) undo.current.shift();
        redo.current = [];
        setDirty(true);
      }
      return next;
    });
  }, []);

  const mapEls = (d: MnemoDoc, ids: Set<string>, fn: (e: MnemoElement) => MnemoElement): MnemoDoc =>
    ({ ...d, elements: d.elements.map((e) => (ids.has(e.id) ? fn(e) : e)) });

  const doUndo = () => {
    const prev = undo.current.pop();
    if (!prev) return;
    redo.current.push(doc);
    setDoc(prev);
    setDirty(true);
  };
  const doRedo = () => {
    const next = redo.current.pop();
    if (!next) return;
    undo.current.push(doc);
    setDoc(next);
    setDirty(true);
  };

  const snapV = (v: number) => (snap ? Math.round(v / doc.grid) * doc.grid : Math.round(v));

  // ---------------------------------------------------------------- координаты
  const toDoc = (clientX: number, clientY: number): Pt => {
    const r = svgRef.current!.getBoundingClientRect();
    return { x: (clientX - r.left) / zoom, y: (clientY - r.top) / zoom };
  };

  const viewportCenter = (): Pt => {
    const s = scrollRef.current;
    if (!s) return { x: 100, y: 100 };
    return { x: (s.scrollLeft + s.clientWidth / 2) / zoom, y: (s.scrollTop + s.clientHeight / 2) / zoom };
  };

  // ---------------------------------------------------------------- действия
  const addElement = (type: ElementType, at?: Pt) => {
    const p = at ?? viewportCenter();
    const def = ELEMENTS[type];
    const el = createElement(type, snapV(p.x - (def.isLine ? 0 : def.size[0] / 2)), snapV(p.y - def.size[1] / 2));
    update((d) => ({ ...d, elements: [...d.elements, el] }));
    setSel(new Set([el.id]));
  };

  const deleteSelected = () => {
    if (sel.size === 0) return;
    update((d) => ({ ...d, elements: d.elements.filter((e) => !sel.has(e.id) || e.locked) }));
    setSel(new Set());
  };

  const duplicate = (els: MnemoElement[], offset = 20) => {
    if (els.length === 0) return;
    const copies = els.map((e) => ({ ...structuredClone(e), id: newId(), x: e.x + offset, y: e.y + offset, locked: false }));
    update((d) => ({ ...d, elements: [...d.elements, ...copies] }));
    setSel(new Set(copies.map((c) => c.id)));
  };

  const zOrder = (front: boolean) => {
    if (sel.size === 0) return;
    update((d) => {
      const picked = d.elements.filter((e) => sel.has(e.id));
      const rest = d.elements.filter((e) => !sel.has(e.id));
      return { ...d, elements: front ? [...rest, ...picked] : [...picked, ...rest] };
    });
  };

  const nudge = (dx: number, dy: number) =>
    update((d) => mapEls(d, sel, (e) => (e.locked ? e : { ...e, x: e.x + dx, y: e.y + dy })));

  const align = (mode: 'left' | 'right' | 'top' | 'bottom' | 'hcenter' | 'vcenter' | 'hdist' | 'vdist') => {
    update((d) => {
      const els = d.elements.filter((e) => sel.has(e.id) && !e.locked);
      if (els.length < 2) return d;
      const bs = els.map((e) => ({ e, b: bounds(e) }));
      const minX = Math.min(...bs.map((x) => x.b.x)), maxX = Math.max(...bs.map((x) => x.b.x + x.b.w));
      const minY = Math.min(...bs.map((x) => x.b.y)), maxY = Math.max(...bs.map((x) => x.b.y + x.b.h));
      const pos = new Map<string, Pt>();
      if (mode === 'hdist' || mode === 'vdist') {
        const h = mode === 'hdist';
        const sorted = [...bs].sort((a, b) => (h ? a.b.x - b.b.x : a.b.y - b.b.y));
        const total = sorted.reduce((s, x) => s + (h ? x.b.w : x.b.h), 0);
        const gap = ((h ? maxX - minX : maxY - minY) - total) / (sorted.length - 1);
        let cur = h ? minX : minY;
        for (const x of sorted) {
          pos.set(x.e.id, h ? { x: x.e.x + (cur - x.b.x), y: x.e.y } : { x: x.e.x, y: x.e.y + (cur - x.b.y) });
          cur += (h ? x.b.w : x.b.h) + gap;
        }
      } else {
        for (const { e, b } of bs) {
          const dx = mode === 'left' ? minX - b.x : mode === 'right' ? maxX - (b.x + b.w)
            : mode === 'hcenter' ? (minX + maxX) / 2 - (b.x + b.w / 2) : 0;
          const dy = mode === 'top' ? minY - b.y : mode === 'bottom' ? maxY - (b.y + b.h)
            : mode === 'vcenter' ? (minY + maxY) / 2 - (b.y + b.h / 2) : 0;
          pos.set(e.id, { x: e.x + dx, y: e.y + dy });
        }
      }
      return { ...d, elements: d.elements.map((e) => (pos.has(e.id) ? { ...e, ...pos.get(e.id)! } : e)) };
    });
  };

  const save = async () => {
    setSaving(true);
    try {
      const r = await api.put<{ version: number }>(`/api/mnemos/${mnemoId}/doc`, { doc, baseVersion });
      setBaseVersion(r.version);
      setDirty(false);
      toast.ok(`Схема сохранена в черновик (в.${r.version}). Операторы увидят её после публикации.`);
      void useMeta.getState().reloadMnemos();
    } catch (e) {
      toast.error(errorText(e));
    } finally {
      setSaving(false);
    }
  };

  const exportJson = () => {
    if (DEMO) {
      // в прототипе браузер не даёт сохранить файл — копируем схему в буфер обмена
      void navigator.clipboard.writeText(JSON.stringify(doc, null, 2))
        .then(() => toast.ok('JSON схемы скопирован в буфер обмена'))
        .catch(() => toast.info('В прототипе выгрузка файла отключена'));
      return;
    }
    const blob = new Blob([JSON.stringify(doc, null, 2)], { type: 'application/json' });
    const a = document.createElement('a');
    a.href = URL.createObjectURL(blob);
    a.download = `${name.replace(/[^\p{L}\p{N}_-]+/gu, '_')}.mnemo.json`;
    a.click();
    URL.revokeObjectURL(a.href);
  };

  const importJson = async (file: File) => {
    try {
      const parsed = normalizeDoc(JSON.parse(await file.text()));
      commit(parsed);
      setSel(new Set());
      toast.info(`Загружено элементов: ${parsed.elements.length}. Сохраните схему.`);
    } catch (e) {
      toast.error(`Неверный файл схемы: ${errorText(e)}`);
    }
  };

  const fit = () => {
    const s = scrollRef.current;
    if (!s) return;
    setZoom(Math.max(0.2, Math.min(2, Math.min((s.clientWidth - 40) / doc.width, (s.clientHeight - 40) / doc.height))));
  };

  // ---------------------------------------------------------------- клавиатура
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      const t = e.target as HTMLElement;
      if (t.closest('input, textarea, select, .modal')) return;
      const ctrl = e.ctrlKey || e.metaKey;
      const selected = doc.elements.filter((x) => sel.has(x.id));
      if (ctrl && e.key.toLowerCase() === 's') { e.preventDefault(); void save(); }
      else if (preview) return;
      else if (ctrl && e.key.toLowerCase() === 'z' && !e.shiftKey) { e.preventDefault(); doUndo(); }
      else if (ctrl && (e.key.toLowerCase() === 'y' || (e.key.toLowerCase() === 'z' && e.shiftKey))) { e.preventDefault(); doRedo(); }
      else if (ctrl && e.key.toLowerCase() === 'c') { clipboard.current = structuredClone(selected); }
      else if (ctrl && e.key.toLowerCase() === 'v') { e.preventDefault(); duplicate(clipboard.current, 20); }
      else if (ctrl && e.key.toLowerCase() === 'd') { e.preventDefault(); duplicate(selected); }
      else if (ctrl && e.key.toLowerCase() === 'a') { e.preventDefault(); setSel(new Set(doc.elements.map((x) => x.id))); }
      else if (e.key === 'Delete' || e.key === 'Backspace') { e.preventDefault(); deleteSelected(); }
      else if (e.key === 'Escape') setSel(new Set());
      else if (e.key.startsWith('Arrow') && sel.size) {
        e.preventDefault();
        const step = e.shiftKey ? doc.grid : 1;
        nudge(e.key === 'ArrowLeft' ? -step : e.key === 'ArrowRight' ? step : 0,
          e.key === 'ArrowUp' ? -step : e.key === 'ArrowDown' ? step : 0);
      }
    };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  });

  useEffect(() => {
    const warn = (e: BeforeUnloadEvent) => { if (dirty) e.preventDefault(); };
    window.addEventListener('beforeunload', warn);
    return () => window.removeEventListener('beforeunload', warn);
  }, [dirty]);

  // ---------------------------------------------------------------- мышь
  const onMouseDown = (e: React.MouseEvent) => {
    if (e.button !== 0) return;
    const p = toDoc(e.clientX, e.clientY);
    const target = e.target as Element;
    const handle = target.getAttribute('data-handle') as Handle | null;
    if (handle) {
      const id = target.getAttribute('data-for')!;
      const el = doc.elements.find((x) => x.id === id)!;
      setDrag({ kind: 'resize', id, handle, start: p, orig: el, before: doc });
      return;
    }
    const g = target.closest('[data-id]');
    const id = g?.getAttribute('data-id');
    if (id) {
      let next = sel;
      if (e.shiftKey) {
        next = new Set(sel);
        if (next.has(id)) next.delete(id); else next.add(id);
      } else if (!sel.has(id)) next = new Set([id]);
      setSel(next);
      const orig = new Map<string, Pt>();
      doc.elements.filter((x) => next.has(x.id) && !x.locked).forEach((x) => orig.set(x.id, { x: x.x, y: x.y }));
      setDrag({ kind: 'move', start: p, orig, moved: false, before: doc });
    } else {
      setDrag({ kind: 'marquee', start: p, cur: p, additive: e.shiftKey });
      if (!e.shiftKey) setSel(new Set());
    }
  };

  const onMouseMove = (e: React.MouseEvent) => {
    if (!drag) return;
    const p = toDoc(e.clientX, e.clientY);
    if (drag.kind === 'move') {
      const dx = p.x - drag.start.x, dy = p.y - drag.start.y;
      if (!drag.moved && Math.abs(dx) < 3 && Math.abs(dy) < 3) return;
      setDoc((d) => ({
        ...d,
        elements: d.elements.map((x) => {
          const o = drag.orig.get(x.id);
          return o ? { ...x, x: snapV(o.x + dx), y: snapV(o.y + dy) } : x;
        }),
      }));
      if (!drag.moved) setDrag({ ...drag, moved: true });
    } else if (drag.kind === 'resize') {
      const o = drag.orig;
      const dx = p.x - drag.start.x, dy = p.y - drag.start.y;
      let { x, y, w, h } = o;
      switch (drag.handle) {
        case 'p1': x = snapV(o.x + dx); y = snapV(o.y + dy); w = o.x + o.w - x; h = o.y + o.h - y; break;
        case 'p2': w = snapV(o.x + o.w + dx) - o.x; h = snapV(o.y + o.h + dy) - o.y; break;
        default: {
          const hd = drag.handle;
          if (hd.includes('e')) w = Math.max(4, snapV(o.x + o.w + dx) - o.x);
          if (hd.includes('s')) h = Math.max(4, snapV(o.y + o.h + dy) - o.y);
          if (hd.includes('w')) { x = Math.min(snapV(o.x + dx), o.x + o.w - 4); w = o.x + o.w - x; }
          if (hd.includes('n')) { y = Math.min(snapV(o.y + dy), o.y + o.h - 4); h = o.y + o.h - y; }
          if (e.shiftKey && o.w && o.h) { const k = o.w / o.h; if (Math.abs(w - o.w) > Math.abs(h - o.h)) h = w / k; else w = h * k; }
        }
      }
      setDoc((d) => ({ ...d, elements: d.elements.map((el) => (el.id === drag.id ? { ...el, x, y, w, h } : el)) }));
    } else {
      setDrag({ ...drag, cur: p });
    }
  };

  const onMouseUp = () => {
    if (!drag) return;
    if (drag.kind === 'move' && drag.moved) commit(doc, drag.before);
    if (drag.kind === 'resize') commit(doc, drag.before);
    if (drag.kind === 'marquee') {
      const r = rectOf(drag.start, drag.cur);
      if (r.w > 3 || r.h > 3) {
        const hit = doc.elements.filter((el) => {
          const b = bounds(el);
          return b.x < r.x + r.w && b.x + b.w > r.x && b.y < r.y + r.h && b.y + b.h > r.y;
        }).map((el) => el.id);
        setSel((s) => new Set(drag.additive ? [...s, ...hit] : hit));
      }
    }
    setDrag(null);
  };

  const onDrop = (e: React.DragEvent) => {
    const type = e.dataTransfer.getData(DND_TYPE) as ElementType;
    if (!type || !ELEMENTS[type]) return;
    e.preventDefault();
    addElement(type, toDoc(e.clientX, e.clientY));
  };

  const selected = doc.elements.filter((e) => sel.has(e.id));

  return (
    <div className="editor">
      <div className="editor-toolbar">
        <b className="editor-title">{name}</b>
        <span className="muted small">версия {baseVersion}{dirty ? ' · изменено' : ''}</span>
        <span className="sep" />
        <button className="primary" onClick={save} disabled={saving || !dirty} title="Ctrl+S">{saving ? 'Сохранение…' : 'Сохранить'}</button>
        <button onClick={doUndo} disabled={preview || undo.current.length === 0} title="Ctrl+Z">↶</button>
        <button onClick={doRedo} disabled={preview || redo.current.length === 0} title="Ctrl+Y">↷</button>
        <span className="sep" />
        <button onClick={() => duplicate(selected)} disabled={!sel.size || preview} title="Ctrl+D">Копия</button>
        <button onClick={() => zOrder(true)} disabled={!sel.size || preview}>Наверх</button>
        <button onClick={() => zOrder(false)} disabled={!sel.size || preview}>Вниз</button>
        <button onClick={deleteSelected} disabled={!sel.size || preview} title="Del">Удалить</button>
        <span className="sep" />
        <button onClick={() => setZoom((z) => Math.max(0.2, z / 1.2))}>−</button>
        <span className="zoom">{Math.round(zoom * 100)}%</span>
        <button onClick={() => setZoom((z) => Math.min(4, z * 1.2))}>+</button>
        <button onClick={fit}>Вписать</button>
        <label className="row small"><input type="checkbox" checked={snap} onChange={(e) => setSnap(e.target.checked)} /> сетка</label>
        <span className="sep" />
        <button className={preview ? 'active' : ''} onClick={() => { setPreview(!preview); setSel(new Set()); }}>
          {preview ? 'Редактировать' : 'Просмотр'}
        </button>
        <button onClick={() => setShowVersions(true)}>Версии</button>
        <button onClick={exportJson}>Экспорт</button>
        <button onClick={() => fileRef.current?.click()}>Импорт</button>
        <input ref={fileRef} type="file" accept=".json,application/json" hidden
          onChange={(e) => { const f = e.target.files?.[0]; if (f) void importJson(f); e.target.value = ''; }} />
        <span className="grow" />
        <button onClick={() => { if (!dirty) onClose(); else void askConfirm('В схеме есть несохранённые изменения. Закрыть редактор без сохранения?', { ok: 'Закрыть без сохранения' }).then((y) => y && onClose()); }}>Закрыть</button>
      </div>

      <div className="editor-body">
        {!preview && (
          <div className="palette">
            {GROUPS.map((g) => (
              <div key={g}>
                <div className="palette-group">{g}</div>
                {Object.values(ELEMENTS).filter((d) => d.group === g).map((d) => (
                  <button key={d.type} className="palette-item" draggable
                    onDragStart={(e) => e.dataTransfer.setData(DND_TYPE, d.type)}
                    onClick={() => addElement(d.type)}>
                    {d.label}
                  </button>
                ))}
              </div>
            ))}
          </div>
        )}

        <div className="editor-canvas" ref={scrollRef} onDragOver={(e) => e.preventDefault()} onDrop={onDrop}>
          {preview ? (
            <div style={{ width: doc.width * zoom, height: doc.height * zoom }}>
              <MnemoView doc={doc} subscriptionKey="editor-preview" />
            </div>
          ) : (
            <svg ref={svgRef} width={doc.width * zoom} height={doc.height * zoom} viewBox={`0 0 ${doc.width} ${doc.height}`}
              className="editor-svg" onMouseDown={onMouseDown} onMouseMove={onMouseMove} onMouseUp={onMouseUp}
              onMouseLeave={onMouseUp}>
              <SvgDefs />
              <defs>
                <pattern id="grid" width={doc.grid} height={doc.grid} patternUnits="userSpaceOnUse">
                  <path d={`M ${doc.grid} 0 L 0 0 0 ${doc.grid}`} fill="none" stroke="rgba(15,23,42,0.08)" strokeWidth={1 / zoom} />
                </pattern>
              </defs>
              <rect width={doc.width} height={doc.height} fill={doc.background} />
              {doc.grid >= 4 && <rect width={doc.width} height={doc.height} fill="url(#grid)" pointerEvents="none" />}
              {doc.elements.map((el) => <ElementView key={el.id} el={el} ctx={editCtx} />)}
              <SelectionOverlay elements={selected} zoom={zoom} />
              {drag?.kind === 'marquee' && (() => {
                const r = rectOf(drag.start, drag.cur);
                return <rect x={r.x} y={r.y} width={r.w} height={r.h} fill="rgba(37,99,235,0.08)" stroke="#2563eb"
                  strokeDasharray="4 3" strokeWidth={1 / zoom} pointerEvents="none" />;
              })()}
            </svg>
          )}
        </div>

        {!preview && (
          <PropertyPanel
            doc={doc}
            selected={selected}
            onDoc={(patch) => update((d) => ({ ...d, ...patch }))}
            onElement={(id, patch) => update((d) => mapEls(d, new Set([id]), (e) => ({ ...e, ...patch })))}
            onProps={(ids, key, value: PropValue) =>
              update((d) => mapEls(d, new Set(ids), (e) => ({ ...e, props: { ...e.props, [key]: value } })))}
            onAlign={align}
          />
        )}
      </div>

      {showVersions && (
        <VersionsDialog mnemoId={mnemoId} current={baseVersion} onClose={() => setShowVersions(false)}
          onLoad={(d, v) => {
            commit(d);
            setSel(new Set());
            setShowVersions(false);
            toast.info(`Загружена версия ${v}. Сохранение создаст новую версию.`);
          }} />
      )}
    </div>
  );
}

function rectOf(a: Pt, b: Pt) {
  return { x: Math.min(a.x, b.x), y: Math.min(a.y, b.y), w: Math.abs(a.x - b.x), h: Math.abs(a.y - b.y) };
}

function SelectionOverlay({ elements, zoom }: { elements: MnemoElement[]; zoom: number }) {
  const hs = 8 / zoom;
  return (
    <g>
      {elements.map((el) => {
        const def = ELEMENTS[el.type];
        if (def.isLine) {
          return (
            <g key={el.id}>
              {(['p1', 'p2'] as const).map((h) => (
                <circle key={h} cx={h === 'p1' ? el.x : el.x + el.w} cy={h === 'p1' ? el.y : el.y + el.h} r={hs / 1.4}
                  className="handle" data-handle={el.locked ? undefined : h} data-for={el.id} />
              ))}
            </g>
          );
        }
        const b = bounds(el);
        const pts: [Handle, number, number][] = [
          ['nw', b.x, b.y], ['n', b.x + b.w / 2, b.y], ['ne', b.x + b.w, b.y], ['e', b.x + b.w, b.y + b.h / 2],
          ['se', b.x + b.w, b.y + b.h], ['s', b.x + b.w / 2, b.y + b.h], ['sw', b.x, b.y + b.h], ['w', b.x, b.y + b.h / 2],
        ];
        return (
          <g key={el.id}>
            <rect x={b.x} y={b.y} width={b.w} height={b.h} fill="none" stroke={el.locked ? '#94a3b8' : '#2563eb'}
              strokeWidth={1 / zoom} strokeDasharray={`${4 / zoom} ${3 / zoom}`} pointerEvents="none" />
            {elements.length === 1 && !el.locked && pts.map(([h, x, y]) => (
              <rect key={h} x={x - hs / 2} y={y - hs / 2} width={hs} height={hs} className={`handle handle-${h}`}
                data-handle={h} data-for={el.id} />
            ))}
          </g>
        );
      })}
    </g>
  );
}

function VersionsDialog(props: { mnemoId: number; current: number; onClose: () => void; onLoad: (d: MnemoDoc, v: number) => void }) {
  const [list, setList] = useState<{ version: number; createdAt: string; createdBy: string | null }[] | null>(null);
  useEffect(() => {
    void api.get<typeof list>(`/api/mnemos/${props.mnemoId}/versions`).then(setList).catch((e) => toast.error(errorText(e)));
  }, [props.mnemoId]);
  const load = async (v: number) => {
    const r = await api.get<{ doc: unknown }>(`/api/mnemos/${props.mnemoId}?version=${v}`);
    props.onLoad(normalizeDoc(r.doc), v);
  };
  return (
    <Modal title="История версий" onClose={props.onClose}>
      {!list ? <p>Загрузка…</p> : list.length === 0 ? <p className="muted">Схема ещё не сохранялась.</p> : (
        <table className="table">
          <thead><tr><th>Версия</th><th>Сохранена</th><th>Автор</th><th /></tr></thead>
          <tbody>
            {list.map((v) => (
              <tr key={v.version}>
                <td>{v.version}{v.version === props.current ? ' (текущая)' : ''}</td>
                <td>{formatTime(v.createdAt)}</td>
                <td>{v.createdBy}</td>
                <td><button onClick={() => void load(v.version)}>Открыть</button></td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </Modal>
  );
}
