import { create } from 'zustand';
import { api } from './api';
import { onRealtime } from './realtime';
import type { ActiveAlarm } from './types';

interface AlarmStore {
  active: ActiveAlarm[];
  byTag: Map<number, ActiveAlarm[]>;
  muted: boolean;
  reload: () => Promise<void>;
  setMuted: (m: boolean) => void;
}

export const useAlarms = create<AlarmStore>((set) => ({
  active: [],
  byTag: new Map(),
  muted: false,
  reload: async () => {
    const active = await api.get<ActiveAlarm[]>('/api/alarms/active');
    const byTag = new Map<number, ActiveAlarm[]>();
    for (const a of active) {
      const list = byTag.get(a.tagId) ?? [];
      list.push(a);
      byTag.set(a.tagId, list);
    }
    set({ active, byTag });
  },
  setMuted: (muted) => set({ muted }),
}));

let timer: ReturnType<typeof setTimeout> | null = null;
function scheduleReload() {
  if (timer) clearTimeout(timer);
  timer = setTimeout(() => void useAlarms.getState().reload().catch(() => {}), 200);
}
onRealtime('alarm', scheduleReload);
onRealtime('resync', scheduleReload);
onRealtime('config', scheduleReload);

/** Худшая (наивысший приоритет) активная тревога тега — для подсветки элементов мнемосхемы. */
export function worstAlarm(list: ActiveAlarm[] | undefined): ActiveAlarm | undefined {
  if (!list) return undefined;
  return list
    .filter((a) => a.active || !a.acked)
    .sort((a, b) => a.priority - b.priority || Number(a.acked) - Number(b.acked))[0];
}

// ------------------------------------------------------------------ звуковая сигнализация
// Звучит, пока есть неквитированные тревоги приоритета 1–2 (браузер требует хотя бы один клик по странице).
let audioCtx: AudioContext | null = null;
let soundTimer: ReturnType<typeof setInterval> | null = null;

function beep(freq: number) {
  try {
    if (!audioCtx || audioCtx.state !== 'running') return;
    const o = audioCtx.createOscillator();
    const g = audioCtx.createGain();
    o.frequency.value = freq;
    o.type = 'square';
    g.gain.value = 0.05;
    o.connect(g).connect(audioCtx.destination);
    o.start();
    o.stop(audioCtx.currentTime + 0.18);
  } catch {
    /* звук недоступен */
  }
}

// контекст звука можно запустить только после действия пользователя
window.addEventListener('pointerdown', () => {
  try {
    audioCtx ??= new AudioContext();
    if (audioCtx.state === 'suspended') void audioCtx.resume();
  } catch { /* нет звука */ }
}, { once: false, passive: true });

useAlarms.subscribe((s) => {
  const loud = !s.muted && s.active.some((a) => !a.acked && a.priority <= 2);
  if (loud && !soundTimer) {
    const p1 = () => useAlarms.getState().active.some((a) => !a.acked && a.priority === 1);
    soundTimer = setInterval(() => beep(p1() ? 880 : 660), 1200);
  } else if (!loud && soundTimer) {
    clearInterval(soundTimer);
    soundTimer = null;
  }
});
