import { LineChart } from 'echarts/charts';
import { DataZoomComponent, GridComponent, LegendComponent, TooltipComponent } from 'echarts/components';
import * as echarts from 'echarts/core';
import { CanvasRenderer } from 'echarts/renderers';
import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { useSearchParams } from 'react-router-dom';
import { Empty, TagPicker } from '../components/ui';
import { api, errorText, qs } from '../lib/api';
import { formatNumber, formatTime, toLocalInput } from '../lib/format';
import { getValue, useTagSubscription, onRealtime } from '../lib/realtime';
import { toast, useMeta } from '../lib/stores';

echarts.use([LineChart, GridComponent, TooltipComponent, LegendComponent, DataZoomComponent, CanvasRenderer]);

type Point = [number, number | null, number | null, number | null, number];
interface HistoryResponse { mode: string; bucketSeconds: number; series: Record<string, Point[]> }

const PRESETS: [string, number][] = [['15 мин', 15], ['1 ч', 60], ['8 ч', 480], ['24 ч', 1440], ['7 сут', 10080], ['30 сут', 43200]];
const COLORS = ['#2563eb', '#dc2626', '#16a34a', '#9333ea', '#ea580c', '#0891b2', '#4d7c0f', '#be185d'];

export function TrendsPage() {
  const [params, setParams] = useSearchParams();
  const tagById = useMeta((s) => s.tagById);
  const tagByName = useMeta((s) => s.tagByName);
  const ids = useMemo(() => (params.get('tags') ?? '').split(',').map(Number).filter((x) => x > 0).slice(0, 8), [params]);
  const [minutes, setMinutes] = useState<number | null>(60);
  const [from, setFrom] = useState(() => toLocalInput(new Date(Date.now() - 3600_000)));
  const [to, setTo] = useState(() => toLocalInput(new Date()));
  const [data, setData] = useState<HistoryResponse | null>(null);
  const [loading, setLoading] = useState(false);
  const chartEl = useRef<HTMLDivElement>(null);
  const chart = useRef<echarts.ECharts | null>(null);
  const live = minutes !== null;

  useTagSubscription('trends', ids);

  const setIds = (next: number[]) => setParams((p) => { p.set('tags', next.join(',')); return p; });

  const range = useCallback((): [Date, Date] => {
    if (minutes !== null) { const t = new Date(); return [new Date(t.getTime() - minutes * 60_000), t]; }
    return [new Date(from), new Date(to)];
  }, [minutes, from, to]);

  const load = useCallback(async () => {
    if (!ids.length) { setData(null); return; }
    const [f, t] = range();
    setLoading(true);
    try {
      setData(await api.get<HistoryResponse>(`/api/history${qs({ tagIds: ids.join(','), from: f.toISOString(), to: t.toISOString(), points: 1200 })}`));
    } catch (e) {
      toast.error(errorText(e));
    } finally {
      setLoading(false);
    }
  }, [ids, range]);

  useEffect(() => { void load(); }, [load]);

  // живой режим: перезапрос раз в 15 с + добавление значений реального времени между запросами
  useEffect(() => {
    if (!live) return;
    const t = setInterval(() => void load(), 15_000);
    const off = onRealtime('resync', () => void load());
    return () => { clearInterval(t); off(); };
  }, [live, load]);

  useEffect(() => {
    if (!live || !ids.length) return;
    const t = setInterval(() => {
      setData((d) => {
        if (!d) return d;
        const series = { ...d.series };
        for (const id of ids) {
          const v = getValue(id);
          if (!v || v[1] === null) continue;
          const arr = series[id] ?? [];
          const last = arr[arr.length - 1];
          if (!last || v[4] > last[0]) series[id] = [...arr, [v[4], v[1], v[1], v[1], v[3]]];
        }
        return { ...d, series };
      });
    }, 2000);
    return () => clearInterval(t);
  }, [live, ids]);

  useEffect(() => {
    if (!chartEl.current) return;
    chart.current = echarts.init(chartEl.current);
    const ro = new ResizeObserver(() => chart.current?.resize());
    ro.observe(chartEl.current);
    return () => { ro.disconnect(); chart.current?.dispose(); chart.current = null; };
  }, []);

  useEffect(() => {
    if (!chart.current) return;
    if (!ids.length) { chart.current.clear(); return; } // пустые оси ломают ECharts
    const units = [...new Set(ids.map((id) => tagById.get(id)?.unit ?? ''))];
    const [f, t] = range();
    chart.current.setOption({
      animation: false,
      color: COLORS,
      grid: { left: 60 + Math.max(0, Math.ceil(units.length / 2) - 1) * 55, right: 40 + Math.max(0, Math.floor(units.length / 2) - 1) * 55 + (units.length > 1 ? 15 : 0), top: 40, bottom: 70 },
      legend: { top: 5, type: 'scroll' },
      tooltip: {
        trigger: 'axis',
        valueFormatter: (v: number | null) => formatNumber(v),
      },
      xAxis: { type: 'time', min: f.getTime(), max: t.getTime() },
      yAxis: units.map((u, i) => ({
        type: 'value', name: u, scale: true, position: i % 2 ? 'right' : 'left',
        offset: Math.floor(i / 2) * 55, splitLine: { show: i === 0 },
      })),
      dataZoom: [{ type: 'inside' }, { type: 'slider', bottom: 15 }],
      series: ids.map((id) => {
        const tag = tagById.get(id);
        const pts = data?.series[id] ?? [];
        return {
          name: tag ? `${tag.name}${tag.description ? ` — ${tag.description}` : ''}` : `#${id}`,
          type: 'line',
          showSymbol: false,
          connectNulls: false,
          step: tag?.dataType === 'bool' ? 'end' : undefined,
          yAxisIndex: Math.max(0, units.indexOf(tag?.unit ?? '')),
          data: pts.map((p) => [p[0], p[4] >= 128 ? null : p[1]]),
        };
      }),
    }, { notMerge: true });
  }, [data, ids, tagById, range]);

  return (
    <div className="page trends">
      <div className="toolbar">
        <div className="chips">
          {ids.map((id, i) => (
            <span key={id} className="chip" style={{ borderColor: COLORS[i % COLORS.length] }}>
              {tagById.get(id)?.name ?? `#${id}`}
              <button className="icon-btn" onClick={() => setIds(ids.filter((x) => x !== id))}>×</button>
            </span>
          ))}
          {ids.length < 8 && (
            <div style={{ width: 240 }}>
              <TagPicker value="" placeholder="+ добавить тег" filter={(t) => t.dataType !== 'string' && !ids.includes(t.id)}
                onChange={(name) => { const t = tagByName.get(name); if (t) setIds([...ids, t.id]); }} />
            </div>
          )}
        </div>
      </div>
      <div className="toolbar">
        {PRESETS.map(([label, m]) => (
          <button key={m} className={minutes === m ? 'active' : ''} onClick={() => setMinutes(m)}>{label}</button>
        ))}
        <button className={minutes === null ? 'active' : ''} onClick={() => setMinutes(null)}>Период</button>
        {minutes === null && (
          <>
            <input type="datetime-local" value={from} onChange={(e) => setFrom(e.target.value)} />
            <input type="datetime-local" value={to} onChange={(e) => setTo(e.target.value)} />
          </>
        )}
        <button onClick={() => void load()}>{loading ? '…' : 'Обновить'}</button>
        <span className="grow" />
        {data && <span className="muted small">{data.mode === 'raw' ? 'исходные значения' : `усреднение ${data.bucketSeconds} с`}{live ? ' · онлайн' : ''}</span>}
        {ids.length === 1 && (() => {
          const [f, t] = range();
          return <a className="btn" href={`/api/history/export${qs({ tagId: ids[0], from: f.toISOString(), to: t.toISOString() })}`}>CSV</a>;
        })()}
      </div>
      {!ids.length && <Empty>Добавьте теги, чтобы построить тренд</Empty>}
      <div ref={chartEl} className="chart" style={{ visibility: ids.length ? 'visible' : 'hidden' }} />
      {data && ids.length > 0 && <div className="muted small">Обновлено {formatTime(Date.now(), false)}</div>}
    </div>
  );
}
