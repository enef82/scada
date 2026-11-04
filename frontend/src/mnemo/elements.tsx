import { memo, useEffect, useMemo, useState, type ReactNode } from 'react';
import { api, qs } from '../lib/api';
import { useAlarms, worstAlarm } from '../lib/alarms';
import { formatNumber, formatValue, qualityCategory, qualityName } from '../lib/format';
import { useTagValue } from '../lib/realtime';
import { useMeta } from '../lib/stores';
import type { ActiveAlarm, Tag } from '../lib/types';
import { requestCommand } from '../components/CommandDialog';
import { numOpt, numOr, str, type MnemoElement } from './model';

export interface RenderCtx {
  editing: boolean;
  canOperate: boolean;
  navigate: (mnemoId: number) => void;
  openFaceplate: (el: MnemoElement) => void;
}

const ALARM_COLORS: Record<number, string> = { 1: '#dc2626', 2: '#ea580c', 3: '#ca8a04', 4: '#7c3aed' };

function useTag(name: unknown): Tag | undefined {
  const byName = useMeta((s) => s.tagByName);
  return typeof name === 'string' && name ? byName.get(name) : undefined;
}

/** Значение тега + качество + худшая тревога. */
function useBinding(name: unknown) {
  const tag = useTag(name);
  const v = useTagValue(tag?.id);
  const alarms = useAlarms((s) => (tag ? s.byTag.get(tag.id) : undefined));
  const alarm = worstAlarm(alarms);
  return {
    tag,
    num: v?.[1] ?? null,
    str: v?.[2] ?? null,
    q: v?.[3],
    cat: tag ? qualityCategory(v?.[3]) : ('good' as const),
    bound: typeof name === 'string' && name !== '',
    alarm,
  };
}

function bit(num: number | null, invert = false): boolean | null {
  if (num === null) return null;
  return invert ? num === 0 : num !== 0;
}

function AlarmFrame(props: { w: number; h: number; alarm?: { priority: number; acked: boolean; active: boolean } }) {
  if (!props.alarm) return null;
  const c = ALARM_COLORS[props.alarm.priority] ?? ALARM_COLORS[2];
  return (
    <rect x={-3} y={-3} width={props.w + 6} height={props.h + 6} fill="none" stroke={c} strokeWidth={3} rx={3}
      className={!props.alarm.acked ? 'blink' : undefined} pointerEvents="none" />
  );
}

/** Недостоверность: штриховка поверх элемента (ISA-101 — не цветом, а формой). */
function BadOverlay(props: { w: number; h: number; cat: string }) {
  if (props.cat === 'good') return null;
  return props.cat === 'bad' ? (
    <rect x={0} y={0} width={props.w} height={props.h} fill="url(#hatch)" pointerEvents="none" />
  ) : (
    <rect x={-1} y={-1} width={props.w + 2} height={props.h + 2} fill="none" stroke="#ca8a04" strokeDasharray="4 3"
      strokeWidth={1.5} pointerEvents="none" />
  );
}

// ------------------------------------------------------------------ элементы

function RectEl({ el }: { el: MnemoElement }) {
  const b = useBinding(el.props.tag);
  const on = b.bound && bit(b.num) === true && str(el.props.onFill);
  return (
    <>
      <rect width={el.w} height={el.h} rx={numOr(el.props.radius, 0)} fill={on ? str(el.props.onFill) : str(el.props.fill, 'none')}
        stroke={str(el.props.stroke, 'none')} strokeWidth={numOr(el.props.strokeWidth, 1)} />
      {b.bound && <BadOverlay w={el.w} h={el.h} cat={b.cat} />}
    </>
  );
}

function EllipseEl({ el }: { el: MnemoElement }) {
  const b = useBinding(el.props.tag);
  const on = b.bound && bit(b.num) === true && str(el.props.onFill);
  return (
    <ellipse cx={el.w / 2} cy={el.h / 2} rx={el.w / 2} ry={el.h / 2} fill={on ? str(el.props.onFill) : str(el.props.fill, 'none')}
      stroke={str(el.props.stroke, 'none')} strokeWidth={numOr(el.props.strokeWidth, 1)} />
  );
}

function LineEl({ el }: { el: MnemoElement }) {
  const b = useBinding(el.props.tag);
  const flow = b.bound && bit(b.num) === true && b.cat !== 'bad';
  const w = numOr(el.props.strokeWidth, 6);
  const markerId = `arrow-${el.id}`;
  const color = flow ? str(el.props.flowColor, '#2563eb') : str(el.props.stroke, '#64748b');
  return (
    <>
      {el.props.arrow && (
        <defs>
          <marker id={markerId} viewBox="0 0 10 10" refX="8" refY="5" markerWidth="4" markerHeight="4" orient="auto-start-reverse">
            <path d="M0,0 L10,5 L0,10 z" fill={color} />
          </marker>
        </defs>
      )}
      <line x1={0} y1={0} x2={el.w} y2={el.h} stroke={color} strokeWidth={w} strokeLinecap="round"
        markerEnd={el.props.arrow ? `url(#${markerId})` : undefined} />
      {flow && (
        <line x1={0} y1={0} x2={el.w} y2={el.h} stroke="rgba(255,255,255,0.65)" strokeWidth={Math.max(1, w / 3)}
          strokeDasharray="6 10" className="flow" />
      )}
      {b.bound && b.cat === 'bad' && (
        <line x1={0} y1={0} x2={el.w} y2={el.h} stroke="#94a3b8" strokeWidth={w} strokeDasharray="2 6" />
      )}
      {/* широкая прозрачная линия — чтобы по трубе было легко попасть мышью в редакторе */}
      <line x1={0} y1={0} x2={el.w} y2={el.h} stroke="transparent" strokeWidth={Math.max(12, w + 8)} />
    </>
  );
}

function TextEl({ el }: { el: MnemoElement }) {
  const align = str(el.props.align, 'start') as 'start' | 'middle' | 'end';
  const x = align === 'middle' ? el.w / 2 : align === 'end' ? el.w : 0;
  return (
    <text x={x} y={el.h / 2} dominantBaseline="central" textAnchor={align} fontSize={numOr(el.props.fontSize, 14)}
      fill={str(el.props.color, '#1e293b')} fontWeight={el.props.bold ? 700 : 400}>
      {str(el.props.text)}
    </text>
  );
}

function ValueEl({ el, ctx }: { el: MnemoElement; ctx: RenderCtx }) {
  const b = useBinding(el.props.tag);
  const fs = numOr(el.props.fontSize, 15);
  const label = str(el.props.label);
  const decimals = numOpt(el.props.decimals);
  const text = b.tag ? formatValue(b.tag, b.num, b.str, decimals) : ctx.editing ? str(el.props.tag, '###') || '###' : '—';
  const unit = el.props.showUnit && b.tag?.unit ? ` ${b.tag.unit}` : '';
  return (
    <>
      <rect width={el.w} height={el.h} rx={2} fill="#f8fafc" stroke="#94a3b8" />
      {label && (
        <text x={6} y={el.h / 2} dominantBaseline="central" fontSize={fs * 0.8} fill="#475569">{label}</text>
      )}
      <text x={el.w - 6} y={el.h / 2} dominantBaseline="central" textAnchor="end" fontSize={fs}
        className={`v v-${b.cat}`} fontFamily="ui-monospace, Consolas, monospace">
        {text}{unit}
      </text>
      <BadOverlay w={el.w} h={el.h} cat={b.cat} />
      <AlarmFrame w={el.w} h={el.h} alarm={b.alarm} />
    </>
  );
}

function LampEl({ el }: { el: MnemoElement }) {
  const b = useBinding(el.props.tag);
  const on = bit(b.num, !!el.props.invert);
  const r = Math.min(el.w, el.h) / 2;
  const fill = b.cat === 'bad' || on === null ? '#e2e8f0' : on ? str(el.props.onColor, '#16a34a') : str(el.props.offColor, '#94a3b8');
  return (
    <>
      <circle cx={el.w / 2} cy={el.h / 2} r={r - 1} fill={fill} stroke="#334155" strokeWidth={1.5} />
      {b.cat === 'bad' && <text x={el.w / 2} y={el.h / 2} dominantBaseline="central" textAnchor="middle" fontSize={r} fill="#475569">?</text>}
      {str(el.props.label) && (
        <text x={el.w / 2} y={el.h + 12} textAnchor="middle" fontSize={11} fill="#334155">{str(el.props.label)}</text>
      )}
      <AlarmFrame w={el.w} h={el.h} alarm={b.alarm} />
    </>
  );
}

function ButtonEl({ el, ctx }: { el: MnemoElement; ctx: RenderCtx }) {
  const b = useBinding(el.props.tag);
  const disabled = !ctx.editing && (!ctx.canOperate || !b.tag?.writable);
  const onClick = () => {
    if (ctx.editing || disabled || !b.tag) return;
    const value = el.props.mode === 'toggle' ? (bit(b.num) ? 0 : 1) : numOr(el.props.value, 1);
    requestCommand({ tag: b.tag, value, label: str(el.props.label, 'Команда') });
  };
  return (
    <g className={disabled ? 'btn-disabled' : 'clickable'} onClick={onClick}>
      <rect width={el.w} height={el.h} rx={4} fill={str(el.props.fill, '#e2e8f0')} stroke="#475569" strokeWidth={1.2} />
      <rect x={1} y={1} width={el.w - 2} height={el.h / 2} rx={3} fill="rgba(255,255,255,0.35)" pointerEvents="none" />
      <text x={el.w / 2} y={el.h / 2} dominantBaseline="central" textAnchor="middle" fontSize={13} fontWeight={600} fill="#1e293b">
        {str(el.props.label)}
      </text>
    </g>
  );
}

function SetpointEl({ el, ctx }: { el: MnemoElement; ctx: RenderCtx }) {
  const b = useBinding(el.props.tag);
  const fs = numOr(el.props.fontSize, 15);
  const can = !ctx.editing && ctx.canOperate && !!b.tag?.writable;
  const text = b.tag ? formatValue(b.tag, b.num, b.str, numOpt(el.props.decimals)) : ctx.editing ? '###' : '—';
  return (
    <g className={can ? 'clickable' : undefined}
      onClick={() => can && b.tag && requestCommand({ tag: b.tag, label: `Уставка ${b.tag.name}` })}>
      <rect width={el.w} height={el.h} rx={2} fill="#fffbeb" stroke="#a16207" strokeWidth={1.2} />
      <text x={el.w - 22} y={el.h / 2} dominantBaseline="central" textAnchor="end" fontSize={fs}
        className={`v v-${b.cat}`} fontFamily="ui-monospace, Consolas, monospace">
        {text}{b.tag?.unit ? ` ${b.tag.unit}` : ''}
      </text>
      <text x={el.w - 6} y={el.h / 2} dominantBaseline="central" textAnchor="end" fontSize={12} fill="#a16207">✎</text>
      <BadOverlay w={el.w} h={el.h} cat={b.cat} />
    </g>
  );
}

function TankEl({ el }: { el: MnemoElement }) {
  const b = useBinding(el.props.tag);
  const min = numOr(el.props.min, 0), max = numOr(el.props.max, 100);
  const frac = b.num === null ? 0 : Math.max(0, Math.min(1, (b.num - min) / (max - min || 1)));
  const fillH = (el.h - 4) * frac;
  return (
    <>
      <rect width={el.w} height={el.h} rx={8} fill="#f1f5f9" stroke="#475569" strokeWidth={2} />
      <rect x={2} y={el.h - 2 - fillH} width={el.w - 4} height={fillH} rx={6}
        fill={b.cat === 'bad' ? '#cbd5e1' : str(el.props.fill, '#60a5fa')} />
      {[0.25, 0.5, 0.75].map((f) => (
        <line key={f} x1={el.w - 10} x2={el.w - 2} y1={el.h - 2 - (el.h - 4) * f} y2={el.h - 2 - (el.h - 4) * f} stroke="#64748b" />
      ))}
      {el.props.showValue && (
        <text x={el.w / 2} y={el.h / 2} dominantBaseline="central" textAnchor="middle" fontSize={14} fontWeight={600}
          className={`v v-${b.cat}`}>
          {formatNumber(b.num, numOpt(el.props.decimals))}{b.tag?.unit ? ` ${b.tag.unit}` : ''}
        </text>
      )}
      <BadOverlay w={el.w} h={el.h} cat={b.cat} />
      <AlarmFrame w={el.w} h={el.h} alarm={b.alarm} />
    </>
  );
}

function BarEl({ el }: { el: MnemoElement }) {
  const b = useBinding(el.props.tag);
  const min = numOr(el.props.min, 0), max = numOr(el.props.max, 100);
  const frac = b.num === null ? 0 : Math.max(0, Math.min(1, (b.num - min) / (max - min || 1)));
  const vertical = !!el.props.vertical;
  const fill = b.cat === 'bad' ? '#cbd5e1' : str(el.props.fill, '#64748b');
  return (
    <>
      <rect width={el.w} height={el.h} fill="#f8fafc" stroke="#94a3b8" />
      {vertical
        ? <rect x={1} y={el.h - 1 - (el.h - 2) * frac} width={el.w - 2} height={(el.h - 2) * frac} fill={fill} />
        : <rect x={1} y={1} width={(el.w - 2) * frac} height={el.h - 2} fill={fill} />}
      <BadOverlay w={el.w} h={el.h} cat={b.cat} />
      <AlarmFrame w={el.w} h={el.h} alarm={b.alarm} />
    </>
  );
}

/** Общая логика оборудования: состояние, авария, качество. */
function useEquipment(el: MnemoElement) {
  const state = useBinding(el.props.stateTag);
  const fault = useBinding(el.props.faultTag);
  const isFault = fault.bound && bit(fault.num) === true;
  const on = bit(state.num);
  const color = isFault
    ? str(el.props.faultColor, '#dc2626')
    : state.cat === 'bad' || on === null ? '#e2e8f0'
    : on ? str(el.props.onColor, '#16a34a') : str(el.props.offColor, '#cbd5e1');
  return { state, fault, isFault, on, color, alarm: worstAlarm([state.alarm, fault.alarm].filter((a): a is ActiveAlarm => !!a)) };
}

function EquipmentShell(props: { el: MnemoElement; ctx: RenderCtx; children: ReactNode; cat: string; alarm?: { priority: number; acked: boolean; active: boolean } }) {
  const { el, ctx } = props;
  return (
    <g className={!ctx.editing ? 'clickable' : undefined} onClick={() => !ctx.editing && ctx.openFaceplate(el)}>
      {props.children}
      {str(el.props.label) && (
        <text x={el.w / 2} y={el.h + 13} textAnchor="middle" fontSize={12} fontWeight={600} fill="#334155">{str(el.props.label)}</text>
      )}
      <BadOverlay w={el.w} h={el.h} cat={props.cat} />
      <AlarmFrame w={el.w} h={el.h} alarm={props.alarm} />
    </g>
  );
}

function PumpEl({ el, ctx }: { el: MnemoElement; ctx: RenderCtx }) {
  const e = useEquipment(el);
  const r = Math.min(el.w, el.h) / 2 - 2;
  const cx = el.w / 2, cy = el.h / 2;
  return (
    <EquipmentShell el={el} ctx={ctx} cat={e.state.bound ? e.state.cat : 'good'} alarm={e.alarm}>
      <circle cx={cx} cy={cy} r={r} fill={e.color} stroke="#1e293b" strokeWidth={2} />
      <path d={`M ${cx - r * 0.55} ${cy - r * 0.6} L ${cx + r * 0.75} ${cy} L ${cx - r * 0.55} ${cy + r * 0.6} Z`}
        fill="none" stroke="#1e293b" strokeWidth={2} strokeLinejoin="round" />
    </EquipmentShell>
  );
}

function MotorEl({ el, ctx }: { el: MnemoElement; ctx: RenderCtx }) {
  const e = useEquipment(el);
  const r = Math.min(el.w, el.h) / 2 - 2;
  return (
    <EquipmentShell el={el} ctx={ctx} cat={e.state.bound ? e.state.cat : 'good'} alarm={e.alarm}>
      <circle cx={el.w / 2} cy={el.h / 2} r={r} fill={e.color} stroke="#1e293b" strokeWidth={2} />
      <text x={el.w / 2} y={el.h / 2} dominantBaseline="central" textAnchor="middle" fontSize={r} fontWeight={700} fill="#1e293b">M</text>
    </EquipmentShell>
  );
}

function ValveEl({ el, ctx }: { el: MnemoElement; ctx: RenderCtx }) {
  const e = useEquipment(el);
  const { w, h } = el;
  return (
    <EquipmentShell el={el} ctx={ctx} cat={e.state.bound ? e.state.cat : 'good'} alarm={e.alarm}>
      <path d={`M 1 1 L ${w / 2} ${h / 2} L 1 ${h - 1} Z M ${w - 1} 1 L ${w / 2} ${h / 2} L ${w - 1} ${h - 1} Z`}
        fill={e.color} stroke="#1e293b" strokeWidth={2} strokeLinejoin="round" />
    </EquipmentShell>
  );
}

function LinkEl({ el, ctx }: { el: MnemoElement; ctx: RenderCtx }) {
  const target = numOpt(el.props.target);
  const mnemos = useMeta((s) => s.mnemos);
  const name = mnemos.find((m) => m.id === target)?.name;
  return (
    <g className={!ctx.editing && target ? 'clickable' : undefined} onClick={() => !ctx.editing && target && ctx.navigate(target)}>
      <rect width={el.w} height={el.h} rx={4} fill="#dbeafe" stroke="#1d4ed8" />
      <text x={el.w / 2} y={el.h / 2} dominantBaseline="central" textAnchor="middle" fontSize={13} fill="#1e3a8a">
        {str(el.props.label) || name || 'Переход'}
      </text>
      {el.props.tooltip === undefined && name && <title>{name}</title>}
    </g>
  );
}

function TrendEl({ el, ctx }: { el: MnemoElement; ctx: RenderCtx }) {
  const tag = useTag(el.props.tag);
  const v = useTagValue(tag?.id);
  const minutes = Math.max(1, numOr(el.props.minutes, 30));
  const [points, setPoints] = useState<[number, number][]>([]);

  // история за окно — при открытии и раз в минуту; между запросами добавляем живые значения
  useEffect(() => {
    if (!tag || ctx.editing) return;
    let cancelled = false;
    const load = async () => {
      const to = new Date(), from = new Date(to.getTime() - minutes * 60_000);
      try {
        const r = await api.get<{ series: Record<string, [number, number | null][]> }>(
          `/api/history${qs({ tagIds: tag.id, from: from.toISOString(), to: to.toISOString(), points: Math.round(el.w) })}`);
        if (!cancelled) setPoints((r.series[tag.id] ?? []).filter((p) => p[1] !== null) as [number, number][]);
      } catch { /* без истории — только живые */ }
    };
    void load();
    const t = setInterval(load, 60_000);
    return () => { cancelled = true; clearInterval(t); };
  }, [tag, minutes, el.w, ctx.editing]);

  useEffect(() => {
    if (!v || v[1] === null || v[3] >= 128) return;
    setPoints((p) => {
      const cutoff = Date.now() - minutes * 60_000;
      const next = [...p.filter((x) => x[0] >= cutoff), [v[4], v[1]!] as [number, number]];
      return next.length > 2000 ? next.slice(-2000) : next;
    });
  }, [v, minutes]);

  const path = useMemo(() => {
    if (points.length < 2) return '';
    const now = Date.now(), t0 = now - minutes * 60_000;
    const ys = points.map((p) => p[1]);
    let lo = numOpt(el.props.min) ?? Math.min(...ys), hi = numOpt(el.props.max) ?? Math.max(...ys);
    if (hi - lo < 1e-9) { hi += 1; lo -= 1; }
    const pad = 4, w = el.w - pad * 2, h = el.h - 18 - pad;
    return points.map((p, i) => {
      const x = pad + ((p[0] - t0) / (now - t0)) * w;
      const y = pad + h - ((p[1] - lo) / (hi - lo)) * h;
      return `${i ? 'L' : 'M'}${x.toFixed(1)},${y.toFixed(1)}`;
    }).join(' ');
  }, [points, minutes, el.w, el.h, el.props.min, el.props.max]);

  return (
    <>
      <rect width={el.w} height={el.h} fill="#ffffff" stroke="#94a3b8" />
      <path d={path} fill="none" stroke={str(el.props.color, '#2563eb')} strokeWidth={1.5} />
      <text x={4} y={el.h - 5} fontSize={11} fill="#475569">{tag?.name ?? (ctx.editing ? 'тег не выбран' : '')}</text>
      <text x={el.w - 4} y={el.h - 5} fontSize={11} textAnchor="end" fill="#475569">
        {tag ? `${formatValue(tag, v?.[1], v?.[2])} ${tag.unit}` : ''} · {minutes} мин
      </text>
    </>
  );
}

// ------------------------------------------------------------------ обёртка

function Visible(props: { el: MnemoElement; editing: boolean; children: ReactNode }) {
  const b = useBinding(props.el.props.visTag);
  if (props.editing || !b.bound) return <>{props.children}</>;
  const on = bit(b.num, !!props.el.props.visInvert);
  return on ? <>{props.children}</> : null;
}

function Body({ el, ctx }: { el: MnemoElement; ctx: RenderCtx }) {
  switch (el.type) {
    case 'rect': return <RectEl el={el} />;
    case 'ellipse': return <EllipseEl el={el} />;
    case 'line': return <LineEl el={el} />;
    case 'text': return <TextEl el={el} />;
    case 'value': return <ValueEl el={el} ctx={ctx} />;
    case 'lamp': return <LampEl el={el} />;
    case 'button': return <ButtonEl el={el} ctx={ctx} />;
    case 'setpoint': return <SetpointEl el={el} ctx={ctx} />;
    case 'tank': return <TankEl el={el} />;
    case 'bar': return <BarEl el={el} />;
    case 'pump': return <PumpEl el={el} ctx={ctx} />;
    case 'motor': return <MotorEl el={el} ctx={ctx} />;
    case 'valve': return <ValveEl el={el} ctx={ctx} />;
    case 'link': return <LinkEl el={el} ctx={ctx} />;
    case 'trend': return <TrendEl el={el} ctx={ctx} />;
    default: return null;
  }
}

/** Элемент схемы: позиционирование, поворот, видимость, подсказка с качеством. */
export const ElementView = memo(function ElementView({ el, ctx }: { el: MnemoElement; ctx: RenderCtx }) {
  const rot = el.rotation ? `rotate(${el.rotation} ${el.w / 2} ${el.h / 2})` : '';
  return (
    <Visible el={el} editing={ctx.editing}>
      <g transform={`translate(${el.x} ${el.y}) ${rot}`} data-id={el.id}>
        <Body el={el} ctx={ctx} />
        {!ctx.editing && <TooltipTitle el={el} />}
      </g>
    </Visible>
  );
});

function TooltipTitle({ el }: { el: MnemoElement }) {
  const b = useBinding(el.props.tag ?? el.props.stateTag);
  const custom = str(el.props.tooltip);
  if (!custom && !b.tag) return null;
  return <title>{[custom, b.tag && `${b.tag.name} — ${b.tag.description}`, b.tag && qualityName(b.q)].filter(Boolean).join('\n')}</title>;
}

/** Общие SVG-определения (штриховка недостоверности). */
export function SvgDefs() {
  return (
    <defs>
      <pattern id="hatch" width="6" height="6" patternUnits="userSpaceOnUse" patternTransform="rotate(45)">
        <rect width="6" height="6" fill="rgba(148,163,184,0.25)" />
        <line x1="0" y1="0" x2="0" y2="6" stroke="rgba(71,85,105,0.55)" strokeWidth="2" />
      </pattern>
    </defs>
  );
}
