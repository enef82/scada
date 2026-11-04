// Связь реального времени (SignalR) + хранилище значений тегов.
// Компонент подписывается на конкретный тег через useTagValue(id) и перерисовывается
// только при изменении этого тега — без лавины перерисовок всего экрана.

import * as signalR from '@microsoft/signalr';
import { useEffect, useSyncExternalStore } from 'react';
import { create } from 'zustand';
import type { TagValueTuple } from './types';

export type LinkState = 'connecting' | 'connected' | 'reconnecting' | 'disconnected';

interface Heartbeat {
  configVersion?: number;
  ts: number;
  tags: number;
  devices: number;
  historianQueue: number;
  spoolBytes: number;
}

interface RtStatus {
  link: LinkState;
  heartbeatAt: number | null;
  heartbeat: Heartbeat | null;
}

export const useRtStatus = create<RtStatus>(() => ({ link: 'connecting', heartbeatAt: null, heartbeat: null }));

type Listener = () => void;
type EventName = 'alarm' | 'command' | 'device' | 'config' | 'resync' | 'published' | 'configApplied';

const values = new Map<number, TagValueTuple>();
const tagListeners = new Map<number, Set<Listener>>();
const eventListeners = new Map<EventName, Set<(payload: unknown) => void>>();
const subscriptions = new Map<string, number[]>();
let connection: signalR.HubConnection | null = null;

/** Транспорт демо-режима: вместо SignalR значения и события приходят от симулятора в браузере. */
export interface DemoTransport {
  start(h: { values: (l: TagValueTuple[]) => void; event: (n: EventName, p?: unknown) => void; heartbeat: (hb: Heartbeat) => void }): void;
  stop(): void;
}
let demo: DemoTransport | null = null;
let demoStarted = false;
export function setDemoTransport(t: DemoTransport) {
  demo = t;
}
export type { EventName };
let resubscribeTimer: ReturnType<typeof setTimeout> | null = null;

function emitTag(id: number) {
  tagListeners.get(id)?.forEach((l) => l());
}

function emit(name: EventName, payload?: unknown) {
  eventListeners.get(name)?.forEach((l) => l(payload));
}

function applyValues(list: TagValueTuple[]) {
  for (const v of list) {
    values.set(v[0], v);
    emitTag(v[0]);
  }
}

export function startRealtime() {
  if (demo) {
    if (demoStarted) return;
    demoStarted = true;
    demo.start({
      values: applyValues,
      event: (n, p) => emit(n, p),
      heartbeat: (hb) => useRtStatus.setState({ heartbeat: hb, heartbeatAt: Date.now(), link: 'connected' }),
    });
    useRtStatus.setState({ link: 'connected' });
    return;
  }
  if (connection) return;
  const conn = new signalR.HubConnectionBuilder()
    .withUrl('/hubs/realtime')
    .withAutomaticReconnect({
      nextRetryDelayInMilliseconds: (ctx) => Math.min(30_000, [0, 1000, 2000, 5000, 10_000][ctx.previousRetryCount] ?? 15_000),
    })
    .configureLogging(signalR.LogLevel.Warning)
    .build();
  connection = conn;

  conn.on('values', (list: TagValueTuple[]) => applyValues(list));
  conn.on('alarm', (p) => emit('alarm', p));
  conn.on('command', (p) => emit('command', p));
  conn.on('device', (p) => emit('device', p));
  conn.on('config', (p) => emit('config', p));
  conn.on('published', (p) => emit('published', p));
  conn.on('configApplied', (p) => emit('configApplied', p));
  conn.on('resync', () => {
    emit('resync');
    scheduleResubscribe();
  });
  conn.on('heartbeat', (hb: Heartbeat) => useRtStatus.setState({ heartbeat: hb, heartbeatAt: Date.now() }));

  conn.onreconnecting(() => useRtStatus.setState({ link: 'reconnecting' }));
  conn.onreconnected(() => {
    useRtStatus.setState({ link: 'connected' });
    scheduleResubscribe(0);
    emit('resync');
  });
  conn.onclose(() => {
    if (connection !== conn) return; // соединение остановлено намеренно
    useRtStatus.setState({ link: 'disconnected' });
    // автоматическое переподключение исчерпано — пробуем снова через 10 с
    setTimeout(() => void connect(), 10_000);
  });
  void connect();
}

async function connect() {
  if (!connection) return;
  if (connection.state !== signalR.HubConnectionState.Disconnected) return;
  try {
    useRtStatus.setState({ link: 'connecting' });
    await connection.start();
    useRtStatus.setState({ link: 'connected' });
    scheduleResubscribe(0);
  } catch {
    useRtStatus.setState({ link: 'disconnected' });
    setTimeout(() => void connect(), 5000);
  }
}

export async function stopRealtime() {
  if (demo) {
    if (demoStarted) demo.stop();
    demoStarted = false;
    return;
  }
  const c = connection;
  connection = null;
  values.clear();
  if (c) await c.stop().catch(() => {});
}

function scheduleResubscribe(delay = 50) {
  if (resubscribeTimer) clearTimeout(resubscribeTimer);
  resubscribeTimer = setTimeout(() => void resubscribe(), delay);
}

async function resubscribe() {
  if (!connection || connection.state !== signalR.HubConnectionState.Connected) return;
  const all = new Set<number>();
  subscriptions.forEach((ids) => ids.forEach((id) => all.add(id)));
  try {
    const snapshot = await connection.invoke<TagValueTuple[]>('Subscribe', [...all]);
    applyValues(snapshot);
  } catch {
    /* переподключение повторит подписку */
  }
}

/** Набор тегов, нужный компоненту (экрану). Сервер получает объединение всех наборов. */
export function useTagSubscription(key: string, ids: number[]) {
  const sig = ids.slice().sort((a, b) => a - b).join(',');
  useEffect(() => {
    subscriptions.set(key, sig ? sig.split(',').map(Number) : []);
    scheduleResubscribe();
    return () => {
      subscriptions.delete(key);
      scheduleResubscribe(500);
    };
  }, [key, sig]);
}

export function getValue(id: number): TagValueTuple | undefined {
  return values.get(id);
}

export function useTagValue(id: number | undefined): TagValueTuple | undefined {
  return useSyncExternalStore(
    (cb) => {
      if (id === undefined) return () => {};
      let set = tagListeners.get(id);
      if (!set) tagListeners.set(id, (set = new Set()));
      set.add(cb);
      return () => set!.delete(cb);
    },
    () => (id === undefined ? undefined : values.get(id)),
  );
}

export function onRealtime(name: EventName, fn: (payload: unknown) => void): () => void {
  let set = eventListeners.get(name);
  if (!set) eventListeners.set(name, (set = new Set()));
  set.add(fn);
  return () => set!.delete(fn);
}

export function useRealtimeEvent(name: EventName, fn: (payload: unknown) => void) {
  useEffect(() => onRealtime(name, fn), [name, fn]);
}

/** Служба сбора жива, если heartbeat приходил недавно. */
export function useAcquisitionAlive(): boolean {
  const at = useRtStatus((s) => s.heartbeatAt);
  const now = useNow(5000);
  return at !== null && now - at < 20_000;
}

/** Текущее время с периодическим обновлением (часы, «устаревание» индикаторов). */
export function useNow(intervalMs = 1000): number {
  return useSyncExternalStore(
    (cb) => {
      const t = setInterval(cb, intervalMs);
      return () => clearInterval(t);
    },
    () => Math.floor(Date.now() / intervalMs) * intervalMs,
  );
}
