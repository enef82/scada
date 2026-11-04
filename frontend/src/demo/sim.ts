// Симулятор КНС и «сервер» в браузере для демо-режима.
// Модель процесса: приток в резервуар, два насоса, задвижка, режим «Автомат» как в ПЛК.
// Тревоги, команды, архив, аудит, конфигурация — в памяти; конфигурация и схемы сохраняются в localStorage.
import type { ActiveAlarm, AlarmDef, AlarmEvent, Area, AuditRow, ChangeItem, ChangeSummary, CommandRow, Device, FieldChange, ObjectType, Perm, Tag, TagValueTuple, ValidationIssue } from '../lib/types';
import { PERMS } from '../lib/types';
import type { DemoTransport, EventName } from '../lib/realtime';
import { boundTagNames, type MnemoDoc } from '../mnemo/model';
import { alarmDefsSeed, areasSeed, assignmentsSeed, devicesSeed, mnemosSeed, personas, rolesSeed, tagsSeed, usersSeed, type DemoAssignment, type DemoRole, type DemoUser, type Persona } from './data';

// ------------------------------------------------------------------ хранилище конфигурации
// Черновик — рабочие списки cfg.devices/tags/alarmDefs/mnemos (их правит инженер).
// Опубликованная конфигурация — неизменяемые снимки cfg.versions; служба сбора и операторы работают по последнему (rt).
interface MnemoRec { id: number; name: string; parentId: number | null; sortOrder: number; areaId: number; versions: { version: number; doc: MnemoDoc; createdAt: string; createdBy: string }[]; updatedAt: string; updatedBy: string; deleted?: boolean }
interface MnemoRef { id: number; name: string; parentId: number | null; sortOrder: number; version: number; areaId: number }
interface Snapshot { devices: Device[]; tags: Tag[]; alarmDefs: AlarmDef[]; mnemos: MnemoRef[] }
interface VersionRec { version: number; createdAt: string; createdBy: string; comment: string; snapshot: Snapshot; changes: ChangeItem[]; warnings: number; rollbackOf: number | null; appliedAt: string | null; appliedMs: number | null }
interface DraftLogRec { ts: string; user: string; type: ObjectType; id: number; name: string; op: 'create' | 'update' | 'delete' }
interface Config { v: number; devices: Device[]; tags: Tag[]; alarmDefs: AlarmDef[]; mnemos: MnemoRec[]; users: DemoUser[]; seq: number; versions: VersionRec[]; draftLog: DraftLogRec[];
  areas: Area[]; roles: DemoRole[]; assignments: DemoAssignment[] }

const STORE_KEY = 'scada-demo-config-v5';

function seedConfig(): Config {
  const at = new Date().toISOString();
  const c: Config = {
    v: 5,
    devices: structuredClone(devicesSeed),
    tags: structuredClone(tagsSeed),
    alarmDefs: structuredClone(alarmDefsSeed),
    mnemos: mnemosSeed().map((m) => ({ id: m.id, name: m.name, parentId: m.parentId, sortOrder: m.sortOrder, areaId: m.areaId, updatedAt: at, updatedBy: 'petrova',
      versions: [{ version: 1, doc: m.doc, createdAt: at, createdBy: 'petrova' }] })),
    users: structuredClone(usersSeed),
    seq: 100,
    versions: [],
    draftLog: [],
    areas: structuredClone(areasSeed),
    roles: structuredClone(rolesSeed),
    assignments: structuredClone(assignmentsSeed),
  };
  c.versions.push({ version: 1, createdAt: new Date(Date.now() - 6 * 86400_000).toISOString(), createdBy: 'petrova', comment: 'Исходная конфигурация',
    snapshot: draftSnapshot(c), changes: [], warnings: 0, rollbackOf: null, appliedAt: new Date(Date.now() - 6 * 86400_000 + 900).toISOString(), appliedMs: 900 });
  return c;
}

function loadConfig(): Config {
  try {
    const raw = localStorage.getItem(STORE_KEY);
    if (raw) {
      const c = JSON.parse(raw) as Config;
      if (c.v === 5) return c;
    }
  } catch { /* хранилище недоступно */ }
  return seedConfig();
}

let cfg = loadConfig();
function saveConfig() {
  try { localStorage.setItem(STORE_KEY, JSON.stringify(cfg)); } catch { /* без сохранения */ }
}
const nextId = () => ++cfg.seq;
const published = () => cfg.versions[cfg.versions.length - 1];
/** Конфигурация, по которой работают служба сбора и операторы. */
let rt: Snapshot = published().snapshot;

// ------------------------------------------------------------------ состояние процесса
export interface Scenarios { link1Lost: boolean; kns2Online: boolean; p1Fault: boolean; overheat: boolean; door: boolean }

const proc = {
  t: 0,
  level: 1.9, inflow: 100, vpos: 1, pressure: 2.1, flow: 90, i1: 41, i2: 0, temp1: 58, kns2: 2.1,
};
const mem: Record<string, number | string> = { sp: 2.8, p1: 1, p2: 0, v1: 1, auto: 1 };
export const scenarios: Scenarios = { link1Lost: false, kns2Online: false, p1Fault: false, overheat: false, door: true };
export let speed = 1;

const noise = (a: number) => (Math.random() - 0.5) * 2 * a;
const approach = (cur: number, target: number, tau: number, dt: number) => cur + (target - cur) * (1 - Math.exp(-dt / tau));

function step(dt: number) {
  const p = proc;
  p.t += dt;
  const fault = scenarios.p1Fault;
  // логика ПЛК в режиме «Автомат»: ведущий Н-1 (при аварии — Н-2), второй насос — при переполнении
  if (mem.auto) {
    const lead = fault ? 'p2' : 'p1', lag = fault ? 'p1' : 'p2';
    if (p.level >= Number(mem.sp)) mem[lead] = 1;
    if (p.level >= Number(mem.sp) + 0.35) mem[lag] = 1;
    if (p.level <= Number(mem.sp) - 0.2) mem[lag] = 0;
    if (p.level <= 1.0) { mem.p1 = 0; mem.p2 = 0; }
  }
  const run1 = mem.p1 && !fault ? 1 : 0;
  const run2 = mem.p2 ? 1 : 0;
  const running = run1 + run2;
  p.vpos = approach(p.vpos, Number(mem.v1), 2, dt);
  p.inflow = 105 + 45 * Math.sin((2 * Math.PI * p.t) / 420) + 8 * Math.sin((2 * Math.PI * p.t) / 53);
  const out = running * 90 * p.vpos;
  p.level = Math.max(0.05, Math.min(3.98, p.level + ((p.inflow - out) / 3600 / 1.6) * dt));
  const pTarget = running ? 1.25 + 0.85 * running + (1 - p.vpos) * 2.7 : 0.3;
  p.pressure = approach(p.pressure, pTarget, 3, dt);
  p.flow = approach(p.flow, out, 2, dt);
  const iTarget = (r: number) => (r ? (p.vpos < 0.3 ? 29 : 41) : 0);
  p.i1 = approach(p.i1, iTarget(run1), 1.5, dt);
  p.i2 = approach(p.i2, iTarget(run2), 1.5, dt);
  p.temp1 = approach(p.temp1, run1 ? (scenarios.overheat ? 82 : 64) : 24, scenarios.overheat ? 25 : 120, dt);
  p.kns2 = 2.1 + 0.9 * Math.sin((2 * Math.PI * p.t) / 300);
}

/** Значение тега по модели; undefined — тег не из модели (тогда работает простой симулятор адреса). */
function modelValue(name: string): number | undefined {
  const p = proc;
  const fault = scenarios.p1Fault;
  switch (name) {
    case 'KNS1_Level': return p.level + noise(0.004);
    case 'KNS1_Level_SP': return Number(mem.sp);
    case 'KNS1_P1_Run': return mem.p1 && !fault ? 1 : 0;
    case 'KNS1_P1_Cmd': return Number(mem.p1);
    case 'KNS1_P1_Fault': return fault ? 1 : 0;
    case 'KNS1_P1_Current': return Math.max(0, p.i1 + (p.i1 > 1 ? noise(0.4) : 0));
    case 'KNS1_P1_Temp': return p.temp1 + noise(0.05);
    case 'KNS1_P2_Run': return mem.p2 ? 1 : 0;
    case 'KNS1_P2_Cmd': return Number(mem.p2);
    case 'KNS1_P2_Current': return Math.max(0, p.i2 + (p.i2 > 1 ? noise(0.4) : 0));
    case 'KNS1_Pressure': return p.pressure + noise(0.015);
    case 'KNS1_Flow': return Math.max(0, p.flow + (p.flow > 1 ? noise(1.5) : 0));
    case 'KNS1_Valve1_Open': return p.vpos > 0.98 ? 1 : 0;
    case 'KNS1_Valve1_Cmd': return Number(mem.v1);
    case 'KNS1_Door': return scenarios.door ? 1 : 0;
    case 'KNS1_Auto': return Number(mem.auto);
    case 'KNS1_P1_Unblock': return 0;
    case 'KNS2_Level': return p.kns2 + noise(0.01);
    case 'KNS2_P1_Run': return p.kns2 > 2.5 ? 1 : 0;
    default: return undefined;
  }
}

/** Простой симулятор для тегов, созданных инженером в демо (адрес sine:/ramp:/random:/memory:). */
function addressValue(tag: Tag): number | string | null {
  const [kind, rest = ''] = tag.address.split(/:(.*)/s);
  const args: Record<string, number> = {};
  let name = '';
  for (const part of rest.split(',')) {
    const [k, v] = part.split('=');
    if (v === undefined) name = k.trim(); else args[k.trim()] = Number(v);
  }
  const t = Date.now() / 1000;
  switch (kind) {
    case 'sine': return (args.offset ?? 50) + (args.amp ?? 10) * Math.sin((2 * Math.PI * t) / (args.period || 60));
    case 'ramp': { const per = args.period || 30; return (args.min ?? 0) + ((args.max ?? 100) - (args.min ?? 0)) * ((t % per) / per); }
    case 'random': return (args.min ?? 0) + ((args.max ?? 100) - (args.min ?? 0)) * Math.random();
    case 'square': { const per = args.period || 10; return t % per < per / 2 ? 1 : 0; }
    case 'memory': {
      const key = 'm_' + rest.split('=')[0];
      if (!(key in mem)) mem[key] = tag.dataType === 'string' ? rest.split('=')[1] ?? '' : Number(rest.split('=')[1] ?? 0);
      return mem[key];
    }
    default: return name ? null : null;
  }
}

function deviceOnline(id: number): boolean {
  const d = rt.devices.find((x) => x.id === id);
  if (!d?.enabled) return false;
  if (id === 1) return !scenarios.link1Lost;
  if (id === 2) return scenarios.kns2Online;
  return true; // устройства, добавленные в демо, «на связи»
}

// ------------------------------------------------------------------ текущие значения, архив
const current = new Map<number, TagValueTuple>();
const history = new Map<number, [number, number | null, number][]>(); // ts, num, quality

function computeValues(now: number): TagValueTuple[] {
  const out: TagValueTuple[] = [];
  for (const t of rt.tags) {
    const dev = rt.devices.find((d) => d.id === t.deviceId);
    const prev = current.get(t.id);
    let tuple: TagValueTuple;
    if (!t.enabled || !dev?.enabled) tuple = [t.id, prev?.[1] ?? null, prev?.[2] ?? null, 133, prev?.[4] ?? now];
    else if (!deviceOnline(t.deviceId)) tuple = [t.id, prev?.[1] ?? null, prev?.[2] ?? null, prev && prev[3] < 128 ? 132 : 130, prev?.[4] ?? now];
    else {
      const mv = modelValue(t.name);
      const v = mv !== undefined ? mv : addressValue(t);
      if (v === null) tuple = [t.id, null, null, 129, now];
      else if (typeof v === 'string') tuple = [t.id, null, v, 0, now];
      else {
        let q = 0;
        if (t.engLow !== null && t.engHigh !== null && (v < Math.min(t.engLow, t.engHigh) || v > Math.max(t.engLow, t.engHigh))) q = 67;
        tuple = [t.id, t.dataType === 'bool' ? (v ? 1 : 0) : v, null, q, now];
      }
    }
    current.set(t.id, tuple);
    out.push(tuple);
  }
  return out;
}

function record(now: number) {
  for (const v of current.values()) {
    const arr = history.get(v[0]) ?? [];
    arr.push([now, v[3] >= 128 ? null : v[1], v[3]]);
    if (arr.length > 120_000) arr.splice(0, 20_000);
    history.set(v[0], arr);
  }
}

/** Предыстория за 24 ч: модель прогоняется с шагом 10 с. */
function prehistory() {
  const now = Date.now();
  const saved = structuredClone(proc), savedMem = { ...mem };
  const door = scenarios.door;
  scenarios.door = false;
  proc.t = -86400;
  for (let ts = now - 86_400_000; ts < now; ts += 10_000) {
    step(10);
    for (const t of rt.tags) {
      const online = t.deviceId === 1 || (t.deviceId === 2 && ts < now - 3 * 3600_000 - 17 * 60_000);
      const mv = modelValue(t.name);
      if (mv === undefined) continue;
      const arr = history.get(t.id) ?? [];
      arr.push([ts, online ? (t.dataType === 'bool' ? (mv ? 1 : 0) : mv) : null, online ? 0 : 130]);
      history.set(t.id, arr);
    }
  }
  Object.assign(proc, saved, { t: 0 });
  Object.assign(mem, savedMem);
  scenarios.door = door;
}

// ------------------------------------------------------------------ тревоги (ISA-18.2: активна/квитирована независимы)
interface AState { active: boolean; acked: boolean; value: number | null; raisedAt: string | null; clearedAt: string | null; ackedAt: string | null; ackedBy: string | null; shelvedUntil: number | null; comment: string | null; pending: number | null }
const alarmState = new Map<number, AState>();
const alarmEvents: AlarmEvent[] = [];
let eventSeq = 1;

const blank = (): AState => ({ active: false, acked: true, value: null, raisedAt: null, clearedAt: null, ackedAt: null, ackedBy: null, shelvedUntil: null, comment: null, pending: null });

function logAlarm(def: AlarmDef, event: AlarmEvent['event'], value: number | null, user: string | null = null, comment: string | null = null) {
  const tag = rt.tags.find((t) => t.id === def.tagId);
  alarmEvents.unshift({ id: eventSeq++, ts: new Date().toISOString(), alarmId: def.id, event, value, user, comment, tagName: tag?.name ?? '?', message: def.message, priority: def.priority });
  if (alarmEvents.length > 5000) alarmEvents.length = 5000;
}

function evaluateAlarms(now: number): boolean {
  let changed = false;
  for (const def of rt.alarmDefs) {
    const st = alarmState.get(def.id) ?? blank();
    alarmState.set(def.id, st);
    if (st.shelvedUntil !== null && st.shelvedUntil <= now) {
      st.shelvedUntil = null;
      logAlarm(def, 'unshelve', st.value);
      changed = true;
    }
    if (!def.enabled) {
      if (st.active || !st.acked) { st.active = false; st.acked = true; changed = true; }
      continue;
    }
    const v = current.get(def.tagId);
    const q = v?.[3] ?? 130;
    const num = q >= 128 ? null : v?.[1] ?? null;
    let cond: boolean | null;
    const lim = def.limitValue ?? 0, h = def.hysteresis;
    switch (def.kind) {
      case 'bad_quality': cond = q >= 128 && q !== 133; break;
      case 'hihi': case 'hi': cond = num === null ? null : st.active ? num > lim - h : num > lim; break;
      case 'lo': case 'lolo': cond = num === null ? null : st.active ? num < lim + h : num < lim; break;
      case 'discrete_on': cond = num === null ? null : num !== 0; break;
      case 'discrete_off': cond = num === null ? null : num === 0; break;
      default: cond = null;
    }
    if (cond === null) continue;
    if (cond) {
      if (st.active) { st.value = num; continue; }
      if (st.shelvedUntil !== null) { st.pending = null; continue; }
      st.pending ??= now;
      if (now - st.pending < def.delayMs) continue;
      Object.assign(st, { pending: null, active: true, acked: false, value: num, raisedAt: new Date(now).toISOString(), clearedAt: null, ackedAt: null, ackedBy: null });
      logAlarm(def, 'raise', num);
      changed = true;
    } else {
      st.pending = null;
      if (st.active) {
        Object.assign(st, { active: false, clearedAt: new Date(now).toISOString(), value: num });
        logAlarm(def, 'clear', num);
        changed = true;
      }
    }
  }
  return changed;
}

function activeAlarms(): ActiveAlarm[] {
  const now = Date.now();
  const list: ActiveAlarm[] = [];
  for (const def of rt.alarmDefs) {
    const st = alarmState.get(def.id);
    if (!st || !(st.active || !st.acked || (st.shelvedUntil ?? 0) > now)) continue;
    const tag = rt.tags.find((t) => t.id === def.tagId);
    list.push({ id: def.id, tagId: def.tagId, tagName: tag?.name ?? '?', tagDescription: tag?.description ?? '', unit: tag?.unit ?? '',
      kind: def.kind, priority: def.priority, message: def.message, active: st.active, acked: st.acked, value: st.value,
      raisedAt: st.raisedAt, clearedAt: st.clearedAt, ackedAt: st.ackedAt, ackedBy: st.ackedBy,
      shelvedUntil: st.shelvedUntil ? new Date(st.shelvedUntil).toISOString() : null, comment: st.comment });
  }
  return list.sort((a, b) => Number(b.active && !b.acked) - Number(a.active && !a.acked) || a.priority - b.priority
    || (b.raisedAt ?? '').localeCompare(a.raisedAt ?? ''));
}

// ------------------------------------------------------------------ журналы
const commands: CommandRow[] = [];
const audit: AuditRow[] = [];
let auditSeq = 1, cmdSeq = 1;

function addAudit(action: string, object: string, details: unknown = {}) {
  audit.unshift({ id: auditSeq++, ts: new Date().toISOString(), user: me()?.login ?? null, action, object, details, ip: '10.0.5.17' });
}

// ------------------------------------------------------------------ пользователь и права (как Scada.Core.Access)
let currentLogin: string | null = 'ivanov';
const me = () => cfg.users.find((u) => u.login === currentLogin && u.enabled) ?? null;
const PERM_NAME = Object.fromEntries(PERMS.map((p) => [p.code, p.name])) as Record<Perm, string>;
const ROOT_ONLY = new Set(PERMS.filter((p) => p.rootOnly).map((p) => p.code));
const ROOT = 1;

function subtree(id: number): number[] {
  const out: number[] = [];
  const stack = [id];
  const seen = new Set<number>();
  while (stack.length) {
    const x = stack.pop()!;
    if (seen.has(x) || !cfg.areas.some((a) => a.id === x)) continue;
    seen.add(x); out.push(x);
    for (const c of cfg.areas) if (c.parentId === x) stack.push(c.id);
  }
  return out;
}
export function areaPathOf(id: number): string {
  const parts: string[] = [];
  const seen = new Set<number>();
  for (let a = cfg.areas.find((x) => x.id === id); a && !seen.has(a.id); a = cfg.areas.find((x) => x.id === a!.parentId)) { seen.add(a.id); parts.unshift(a.name); }
  return parts.join(' / ') || `#${id}`;
}
const within = (id: number, ancestor: number) => subtree(ancestor).includes(id);

function permsFor(login: string | null): Map<Perm, Set<number>> {
  const m = new Map<Perm, Set<number>>();
  const now = Date.now();
  for (const a of cfg.assignments) {
    if (a.principalType !== 'user' || a.principal.toLowerCase() !== (login ?? '').toLowerCase()) continue;
    if (a.validUntil && Date.parse(a.validUntil) <= now) continue;
    const role = cfg.roles.find((r) => r.id === a.roleId);
    if (!role) continue;
    const areas = subtree(a.areaId);
    for (const p of role.permissions) {
      if (ROOT_ONLY.has(p) && a.areaId !== ROOT) continue;
      const set = m.get(p) ?? new Set<number>();
      areas.forEach((x) => set.add(x));
      m.set(p, set);
    }
  }
  return m;
}
const has = (perm: Perm, area?: number | null, login = currentLogin) => {
  const set = permsFor(login).get(perm);
  if (!set) return false;
  return area === undefined || area === null ? set.size > 0 : set.has(area || ROOT);
};
/** Области с правом; null — везде. */
const areasOf = (perm: Perm) => (has(perm, ROOT) ? null : [...(permsFor(currentLogin).get(perm) ?? [])]);
const visible = (perm: Perm, area: number) => { const a = areasOf(perm); return a === null || a.includes(area); };

function denyUnless(perm: Perm, area: number, what: string) {
  if (has(perm, area)) return;
  addAudit('access_denied', what, { perm, area: areaPathOf(area) });
  bad(`Нет права «${PERM_NAME[perm]}» на область «${areaPathOf(area)}»`, 403);
}

function rolesOf(login: string): string[] {
  const now = Date.now();
  return [...new Set(cfg.assignments.filter((a) => a.principalType === 'user' && a.principal === login && !(a.validUntil && Date.parse(a.validUntil) <= now))
    .map((a) => cfg.roles.find((r) => r.id === a.roleId)?.name ?? '?'))];
}

/** Демо: у каждой персоны свой «сеанс» — второй подтверждающий должен быть с другого. */
const sessionOf = (login: string | null) => `demo-${login}`;

export function switchPersona(key: Persona) {
  currentLogin = personas.find((p) => p.key === key)!.login;
  addAudit('login', `user:${currentLogin}`, { demo: 'смена персоны' });
  return meDto();
}
export const meDto = () => {
  const u = me();
  if (!u) return null;
  const perms = permsFor(u.login);
  const permissions: Record<string, number[] | 'all'> = {};
  for (const [p, set] of perms) if (set.size) permissions[p] = set.has(ROOT) ? 'all' : [...set].sort((a, b) => a - b);
  return { login: u.login, displayName: u.displayName, source: u.source, emergency: u.emergency, mustChangePassword: false,
    permissions, roles: rolesOf(u.login), groups: [] as string[] };
};
export const currentUser = () => currentLogin;

// ------------------------------------------------------------------ события для заданий оценки
export const marks = new Set<string>();
type MarkListener = () => void;
const markListeners = new Set<MarkListener>();
export function onMarks(fn: MarkListener) { markListeners.add(fn); return () => markListeners.delete(fn); }
function mark(m: string) { marks.add(m); markListeners.forEach((f) => f()); }
export const procState = () => ({ auto: Number(mem.auto), p2: Number(mem.p2), sp: Number(mem.sp) });

// ------------------------------------------------------------------ транспорт реального времени
let handlers: Parameters<DemoTransport['start']>[0] | null = null;
let timer: ReturnType<typeof setInterval> | null = null;
let lastRecord = 0, lastHb = 0, tickNo = 0;
const devState = new Map<number, string>();
const devErrors = new Map<number, number>();

function fire(n: EventName, p?: unknown) { handlers?.event(n, p); }

function tick() {
  const now = Date.now();
  step(0.5 * speed);
  const vals = computeValues(now);
  handlers?.values(vals);
  if (now - lastRecord >= 1000) { record(now); lastRecord = now; }
  if (evaluateAlarms(now)) fire('alarm', {});
  if (awaiting.size) expireAwaiting();
  for (const d of rt.devices) {
    const s = !d.enabled ? 'disabled' : deviceOnline(d.id) ? 'online' : 'error';
    if (s === 'error') devErrors.set(d.id, (devErrors.get(d.id) ?? 0) + (tickNo % 2 ? 0 : 1));
    if (devState.get(d.id) !== s) { devState.set(d.id, s); fire('device', d.id); }
  }
  if (now - lastHb >= 5000) {
    lastHb = now;
    handlers?.heartbeat({ ts: now, configVersion: published().version, tags: rt.tags.length, devices: rt.devices.filter((d) => d.enabled).length, historianQueue: 0, spoolBytes: 0 });
  }
  tickNo++;
}

export const transport: DemoTransport = {
  start(h) {
    handlers = h;
    computeValues(Date.now());
    h.heartbeat({ ts: Date.now(), tags: rt.tags.length, devices: rt.devices.length, historianQueue: 0, spoolBytes: 0 });
    timer ??= setInterval(tick, 500);
  },
  stop() {
    handlers = null;
    if (timer) clearInterval(timer);
    timer = null;
  },
};

export function setSpeed(s: number) { speed = s; }

export function setScenario<K extends keyof Scenarios>(k: K, v: Scenarios[K]) {
  scenarios[k] = v;
  if (k === 'p1Fault' && v) mem.p1 = 0;
  tick();
}

export function resetDemo() {
  try { localStorage.removeItem(STORE_KEY); } catch { /* нет */ }
  cfg = seedConfig();
  rt = published().snapshot;
  Object.assign(scenarios, { link1Lost: false, kns2Online: false, p1Fault: false, overheat: false, door: true });
  Object.assign(mem, { sp: 2.8, p1: 1, p2: 0, v1: 1, auto: 1 });
  alarmState.clear();
  alarmEvents.length = 0;
  commands.length = 0;
  fire('config', 'all');
  fire('alarm', {});
}

// ------------------------------------------------------------------ публикация конфигурации
function draftSnapshot(c: Config = cfg): Snapshot {
  return structuredClone({
    devices: c.devices, tags: c.tags, alarmDefs: c.alarmDefs,
    mnemos: c.mnemos.filter((m) => !m.deleted).map((m) => ({ id: m.id, name: m.name, parentId: m.parentId, sortOrder: m.sortOrder, version: m.versions.at(-1)?.version ?? 0, areaId: m.areaId })),
  });
}

function mnemoDoc(id: number, version: number): MnemoDoc | null {
  return cfg.mnemos.find((m) => m.id === id)?.versions.find((v) => v.version === version)?.doc ?? null;
}

const IGNORED = new Set(['id', 'rowVersion', 'state', 'lastOkAt', 'lastError', 'errorCount', 'avgLatencyMs', 'effectiveAreaId']);
// изменения, которые влияют на безопасность и управление, — подсвечиваются в списке
const SENSITIVE = new Set(['address', 'deviceId', 'writable', 'writeMin', 'writeMax', 'writeConfirm', 'limitValue', 'hysteresis', 'enabled', 'priority', 'endpoint', 'protocol', 'areaId', 'commandClass']);
const same = (a: unknown, b: unknown) => JSON.stringify(a ?? null) === JSON.stringify(b ?? null);

function diff(prev: Snapshot, next: Snapshot): ChangeItem[] {
  const out: ChangeItem[] = [];
  const tagName = (id: number) => next.tags.find((t) => t.id === id)?.name ?? prev.tags.find((t) => t.id === id)?.name ?? `#${id}`;
  const lists: [ObjectType, { id: number }[], { id: number }[], (x: any) => string][] = [
    ['device', prev.devices, next.devices, (x: Device) => x.name],
    ['tag', prev.tags, next.tags, (x: Tag) => x.name],
    ['alarm', prev.alarmDefs, next.alarmDefs, (x: AlarmDef) => `${tagName(x.tagId)}: ${x.message || x.kind}`],
    ['mnemo', prev.mnemos, next.mnemos, (x: MnemoRef) => x.name],
  ];
  for (const [type, a, b, name] of lists) {
    const before = new Map(a.map((x) => [x.id, x as Record<string, unknown>]));
    const after = new Set(b.map((x) => x.id));
    for (const x of b as Record<string, unknown>[]) {
      const p = before.get(x.id as number);
      if (!p) { out.push({ objectType: type, id: x.id as number, name: name(x), op: 'created', fields: [], highlight: type === 'tag' && !!x.writable }); continue; }
      const fields: FieldChange[] = [];
      for (const k of new Set([...Object.keys(p), ...Object.keys(x)])) {
        if (IGNORED.has(k) || same(p[k], x[k])) continue;
        // версия схемы меняется при каждом сохранении — считаем изменением только другое содержимое
        if (type === 'mnemo' && k === 'version' && same(mnemoDoc(x.id as number, p[k] as number), mnemoDoc(x.id as number, x[k] as number))) continue;
        fields.push({ field: k, old: p[k] as never, new: x[k] as never });
      }
      if (fields.length) out.push({ objectType: type, id: x.id as number, name: name(x), op: 'updated', fields, highlight: fields.some((f) => SENSITIVE.has(f.field)) });
    }
    for (const p of a) if (!after.has(p.id)) out.push({ objectType: type, id: p.id, name: name(p), op: 'deleted', fields: [], highlight: type !== 'mnemo' });
  }
  return out;
}

function summarize(changes: ChangeItem[]): ChangeSummary {
  const s: ChangeSummary = {};
  for (const c of changes) { const t = (s[c.objectType] ??= {}); t[c.op] = (t[c.op] ?? 0) + 1; }
  return s;
}

/** Подмножество проверок ConfigValidator сервера: ошибки блокируют публикацию, предупреждения требуют подтверждения. */
const CLASS_RU = { normal: 'Обычный', important: 'Важный', critical: 'Критичный' } as const;

function validate(s: Snapshot, prev: Snapshot): { errors: ValidationIssue[]; warnings: ValidationIssue[] } {
  const all: ValidationIssue[] = [];
  const add = (code: string, level: 'error' | 'warning', message: string, objectType: ObjectType, objectId: number) =>
    all.push({ code, level, message, objectType, objectId });
  const devices = new Map(s.devices.map((d) => [d.id, d]));
  const tags = new Map(s.tags.map((t) => [t.id, t]));
  const tagNames = new Set(s.tags.map((t) => t.name));
  const byAddr = new Map<string, Tag[]>();
  for (const t of s.tags) {
    const was = prev.tags.find((x) => x.id === t.id);
    if (!devices.has(t.deviceId)) add('tag-device-missing', 'error', `Тег ${t.name}: устройство удалено или не существует`, 'tag', t.id);
    if (!t.address?.trim()) add('address-invalid', 'error', `Тег ${t.name}: не задан адрес`, 'tag', t.id);
    const sc = [t.rawLow, t.rawHigh, t.engLow, t.engHigh];
    if (sc.some((v) => v === null) && sc.some((v) => v !== null))
      add('scaling-partial', 'error', `Тег ${t.name}: масштабирование задано не полностью — заполните все четыре поля или оставьте их пустыми`, 'tag', t.id);
    else if (t.rawLow !== null && t.rawLow === t.rawHigh) add('scaling-equal', 'error', `Тег ${t.name}: сырые мин. и макс. совпадают — масштаб не определён`, 'tag', t.id);
    if (t.writable && t.dataType !== 'bool' && t.dataType !== 'string' && (t.writeMin === null || t.writeMax === null))
      add('write-no-range', 'warning', `Тег ${t.name} доступен для записи без ограничения диапазона`, 'tag', t.id);
    if (t.writable && was && !was.writable) add('became-writable', 'warning', `Тег ${t.name} стал доступен для записи — операторы смогут отправлять на него команды`, 'tag', t.id);
    if (was?.writeConfirm && !t.writeConfirm) add('confirm-removed', 'warning', `Тег ${t.name}: снято обязательное подтверждение команды`, 'tag', t.id);
    const rank = { normal: 0, important: 1, critical: 2 } as const;
    if (t.writable && was?.writable && rank[t.commandClass ?? 'normal'] < rank[was.commandClass ?? 'normal'])
      add('class-lowered', 'warning', `Тег ${t.name}: класс команды понижен «${CLASS_RU[was.commandClass]}» → «${CLASS_RU[t.commandClass]}» — подтверждение станет проще`, 'tag', t.id);
    if (t.areaId !== null && !cfg.areas.some((a) => a.id === t.areaId)) add('area-missing', 'error', `Тег ${t.name}: область #${t.areaId} не существует`, 'tag', t.id);
    const key = `${t.deviceId}|${t.address}`;
    byAddr.set(key, [...(byAddr.get(key) ?? []), t]);
  }
  for (const group of byAddr.values()) {
    if (group.length < 2) continue;
    const names = group.map((t) => t.name).join(', ');
    if (group.some((t) => t.writable)) add('address-overlap', 'error', `Теги ${names} ссылаются на один адрес, и среди них есть записываемый`, 'tag', group[0].id);
    else add('address-overlap-ro', 'warning', `Теги ${names} читают один и тот же адрес`, 'tag', group[0].id);
  }
  for (const d of s.devices) {
    const was = prev.devices.find((x) => x.id === d.id);
    if (!s.tags.some((t) => t.deviceId === d.id)) add('device-no-tags', 'warning', `Устройство ${d.name}: нет ни одного тега — опрашиваться не будет`, 'device', d.id);
    if (!d.enabled && was?.enabled) add('device-disabled', 'warning', `Устройство ${d.name} отключается — все его теги получат качество «отключено»`, 'device', d.id);
    const touched = !was || was.enabled !== d.enabled || was.pollMs !== d.pollMs || was.timeoutMs !== d.timeoutMs;
    if (touched && d.enabled && d.protocol !== 'simulator' && d.pollMs < d.timeoutMs) add('poll-lt-timeout', 'warning', `Устройство ${d.name}: период опроса (${d.pollMs} мс) меньше тайм-аута (${d.timeoutMs} мс)`, 'device', d.id);
  }
  for (const a of s.alarmDefs) {
    const t = tags.get(a.tagId);
    if (!t) { add('alarm-tag-missing', 'error', `Тревога «${a.message}»: тег удалён`, 'alarm', a.id); continue; }
    if (['hihi', 'hi', 'lo', 'lolo'].includes(a.kind) && a.limitValue === null) add('alarm-no-limit', 'error', `Тревога по тегу ${t.name}: не задана уставка`, 'alarm', a.id);
    if (!a.message?.trim()) add('alarm-no-message', 'error', `Тревога по тегу ${t.name}: пустой текст сообщения — оператор не поймёт, что случилось`, 'alarm', a.id);
    if (a.limitValue !== null && t.engLow !== null && t.engHigh !== null && (a.limitValue < Math.min(t.engLow, t.engHigh) || a.limitValue > Math.max(t.engLow, t.engHigh)))
      add('alarm-out-of-range', 'warning', `Тревога по тегу ${t.name}: уставка ${a.limitValue} вне шкалы ${t.engLow}…${t.engHigh} ${t.unit} — не сработает никогда`, 'alarm', a.id);
  }
  for (const m of s.mnemos) {
    const doc = mnemoDoc(m.id, m.version);
    if (!doc?.elements.length) { add('mnemo-empty', 'warning', `Схема «${m.name}» пустая`, 'mnemo', m.id); continue; }
    for (const name of boundTagNames(doc))
      if (!tagNames.has(name)) add('mnemo-tag-missing', 'error', `Схема «${m.name}»: элемент привязан к несуществующему тегу ${name}`, 'mnemo', m.id);
  }
  return { errors: all.filter((i) => i.level === 'error'), warnings: all.filter((i) => i.level === 'warning') };
}

/** Вернуть черновик к снимку (сброс черновика, откат). Схемы не удаляются физически — только помечаются. */
function restore(snap: Snapshot) {
  cfg.devices = structuredClone(snap.devices);
  cfg.tags = structuredClone(snap.tags);
  cfg.alarmDefs = structuredClone(snap.alarmDefs);
  for (const rec of cfg.mnemos) {
    const r = snap.mnemos.find((x) => x.id === rec.id);
    if (!r) { rec.deleted = true; continue; }
    Object.assign(rec, { deleted: false, name: r.name, parentId: r.parentId, sortOrder: r.sortOrder });
    const doc = mnemoDoc(rec.id, r.version);
    const last = rec.versions.at(-1);
    if (doc && last && !same(last.doc, doc))
      rec.versions.push({ version: last.version + 1, doc: structuredClone(doc), createdAt: new Date().toISOString(), createdBy: currentLogin ?? '?' });
  }
  cfg.draftLog = [];
}

function publishSnapshot(snap: Snapshot, comment: string, warnings: number, rollbackOf: number | null): number {
  const changes = diff(rt, snap);
  const v: VersionRec = { version: published().version + 1, createdAt: new Date().toISOString(), createdBy: currentLogin ?? '?', comment, snapshot: snap,
    changes, warnings, rollbackOf, appliedAt: null, appliedMs: null };
  cfg.versions.push(v);
  cfg.draftLog = [];
  saveConfig();
  addAudit(rollbackOf ? 'config_rollback' : 'config_publish', `config:${v.version}`, { comment, rollbackOf, warnings, changes: changes.length });
  fire('published', v.version);
  fire('config', 'all');
  mark('publish');
  const lampDoc = snap.mnemos.find((x) => x.id === 1);
  if (lampDoc && mnemoDoc(1, lampDoc.version)?.elements.some((e) => e.type === 'lamp' && e.props.tag === 'KNS1_P2_Run')) mark('lamp');
  // служба сбора перечитывает снимок и подтверждает применение
  const ms = Math.round(600 + Math.random() * 900);
  setTimeout(() => {
    rt = snap;
    for (const id of [...alarmState.keys()]) if (!rt.alarmDefs.some((a) => a.id === id)) alarmState.delete(id);
    for (const id of [...current.keys()]) if (!rt.tags.some((t) => t.id === id)) current.delete(id);
    Object.assign(v, { appliedAt: new Date().toISOString(), appliedMs: ms });
    saveConfig();
    tick();
    fire('configApplied', { version: v.version, ms });
    fire('alarm', {});
  }, ms);
  return v.version;
}

function versionDto(v: VersionRec) {
  return { version: v.version, createdAt: v.createdAt, createdBy: v.createdBy, comment: v.comment, rollbackOf: v.rollbackOf,
    appliedAt: v.appliedAt, appliedMs: v.appliedMs, summary: summarize(v.changes), warnings: v.warnings };
}

function checkPassword(password: unknown, action: string) {
  // в демо подходит любой непустой пароль
  if (!String(password ?? '')) { addAudit('publish_denied', 'config', { action, reason: 'пароль' }); bad('Неверный пароль', 403); }
}

// ------------------------------------------------------------------ HTTP API (подменяет fetch)
class HttpError extends Error { constructor(public status: number, msg: string, public extra?: object) { super(msg); } }
function bad(msg: string, status = 400, extra?: object): never { throw new HttpError(status, msg, extra); }
/** Право хотя бы в одной области (любое из перечисленных). */
const need = (...perms: Perm[]) => { if (!me()) bad('Не выполнен вход', 401); if (!perms.some((p) => has(p))) bad('Недостаточно прав', 403); };

const snapTagArea = (s: { tags: Tag[]; devices: Device[] }, t: Tag) => t.areaId ?? s.devices.find((d) => d.id === t.deviceId)?.areaId ?? ROOT;
const draftTagArea = (t: Tag) => snapTagArea(cfg, t);
const draftTagAreaById = (id: number) => { const t = cfg.tags.find((x) => x.id === id); return t ? draftTagArea(t) : ROOT; };
const rtTagArea = (id: number) => { const t = rt.tags.find((x) => x.id === id); return t ? snapTagArea(rt, t) : 0; };
const rtAlarmArea = (id: number) => { const a = rt.alarmDefs.find((x) => x.id === id); return a ? rtTagArea(a.tagId) : 0; };
const tagDto = (t: Tag) => ({ ...t, effectiveAreaId: draftTagArea(t) });
const strip = <T extends object>(b: T) => { const x = { ...b } as Record<string, unknown>; delete x.effectiveAreaId; delete x.state; delete x.lastOkAt; delete x.lastError; delete x.errorCount; delete x.avgLatencyMs; return x as T; };

function mnemoDto(m: MnemoRec) {
  return { id: m.id, name: m.name, parentId: m.parentId, sortOrder: m.sortOrder, currentVersion: m.versions.at(-1)?.version ?? 0, updatedAt: m.updatedAt, updatedBy: m.updatedBy,
    publishedVersion: rt.mnemos.find((x) => x.id === m.id)?.version ?? null, areaId: m.areaId };
}

function devicesDto() {
  return cfg.devices.filter((d) => visible('view', d.areaId)).map((d) => {
    const live = rt.devices.some((x) => x.id === d.id); // ещё не опубликовано — служба сбора его не опрашивает
    const s = devState.get(d.id) ?? (deviceOnline(d.id) ? 'online' : 'error');
    return { ...d, state: !live ? null : d.enabled ? s : 'disabled', lastOkAt: s === 'online' ? new Date().toISOString() : d.id === 2 ? new Date(Date.now() - (3 * 3600 + 17 * 60) * 1000).toISOString() : null,
      lastError: s === 'error' ? `Нет ответа за ${d.timeoutMs} мс (${d.endpoint})` : null, errorCount: devErrors.get(d.id) ?? 0,
      avgLatencyMs: s === 'online' ? (d.id === 1 ? 6.4 : 11.2) : null };
  });
}

function historyQuery(ids: number[], from: number, to: number, points: number) {
  const span = (to - from) / 1000;
  const bucket = Math.max(1, Math.ceil(span / points));
  const series: Record<number, (number | null)[][]> = {};
  for (const id of ids) {
    const src = (history.get(id) ?? []).filter((p) => p[0] >= from && p[0] <= to);
    if (bucket <= 2) { series[id] = src.map((p) => [p[0], p[1], p[1], p[1], p[2]]); continue; }
    const out: (number | null)[][] = [];
    let b = -1, sum = 0, cnt = 0, mn = Infinity, mx = -Infinity, q = 0;
    const flush = () => { if (b >= 0) out.push([from + b * bucket * 1000, cnt ? sum / cnt : null, cnt ? mn : null, cnt ? mx : null, q]); };
    for (const p of src) {
      const nb = Math.floor((p[0] - from) / 1000 / bucket);
      if (nb !== b) { flush(); b = nb; sum = 0; cnt = 0; mn = Infinity; mx = -Infinity; q = 0; }
      if (p[1] !== null) { sum += p[1]; cnt++; mn = Math.min(mn, p[1]); mx = Math.max(mx, p[1]); }
      q = Math.max(q, p[2]);
    }
    flush();
    series[id] = out;
  }
  return { mode: bucket <= 2 ? 'raw' : 'aggregate', bucketSeconds: bucket <= 2 ? 0 : bucket, series };
}

// ------------------------------------------------------------------ команды по классам критичности
const CMD_TTL = 15, CONFIRM_S = 60;
interface Awaiting { row: CommandRow; tag: Tag; value: number | string; session: string; expires: number }
const awaiting = new Map<number, Awaiting>();

async function executeCommand(body: { tagId: number; value?: number; valueStr?: string; confirmed?: boolean; password?: string; comment?: string }) {
  if (!me()) bad('Не выполнен вход', 401);
  // свойства тега — из опубликованной версии, по которой работает служба сбора
  const tag = rt.tags.find((t) => t.id === body.tagId) ?? bad('Тег не найден', 404);
  const area = rtTagArea(tag.id);
  const cls = tag.commandClass ?? 'normal';
  const perm: Perm = cls === 'critical' ? 'cmd.critical' : tag.dataType === 'bool' ? 'cmd.control' : 'cmd.setpoint';
  if (!tag.enabled || !tag.writable) bad('Тег недоступен для записи', 403);
  denyUnless(perm, area, `tag:${tag.id}`);
  if ((cls !== 'normal' || tag.writeConfirm) && !body.confirmed) bad('Требуется подтверждение оператора', 428);
  if (cls !== 'normal') {
    if (!String(body.comment ?? '').trim()) bad('Для этой команды обязателен комментарий');
    if (!String(body.password ?? '')) { addAudit('command_denied', `tag:${tag.id}`, { reason: 'password' }); bad('Неверный пароль', 403); }
  }
  const value = tag.dataType === 'string' ? body.valueStr : body.value;
  if (value === undefined || value === null) bad('Не задано значение');
  if (typeof value === 'number' && ((tag.writeMin !== null && value < tag.writeMin) || (tag.writeMax !== null && value > tag.writeMax)))
    bad(`Значение вне диапазона [${tag.writeMin ?? '−∞'}; ${tag.writeMax ?? '+∞'}]`);

  const critical = cls === 'critical';
  const row: CommandRow = { id: cmdSeq++, tagId: tag.id, tagName: tag.name, valueNum: typeof value === 'number' ? value : null,
    valueStr: typeof value === 'string' ? value : null, requestedBy: me()!.login, requestedAt: new Date().toISOString(),
    expiresAt: new Date(Date.now() + (critical ? CONFIRM_S : CMD_TTL) * 1000).toISOString(), status: critical ? 'awaiting_confirm' : 'executing',
    result: null, readbackNum: null, finishedAt: null, commandClass: cls, comment: body.comment?.trim() || null, confirmedBy: null };
  commands.unshift(row);
  if (critical) {
    awaiting.set(row.id, { row, tag, value, session: sessionOf(currentLogin), expires: Date.now() + CONFIRM_S * 1000 });
    addAudit('command_requested', `tag:${tag.id}`, { command: row.id, tag: tag.name, value, commandClass: cls, comment: row.comment });
    fire('confirmRequest', row.id);
    mark('criticalRequested');
    return { id: row.id, status: 'awaiting_confirm', confirmSeconds: CONFIRM_S };
  }
  return runCommand(row, tag, value, { confirmation: cls === 'important' ? 'password+comment' : tag.writeConfirm ? 'dialog' : 'none' });
}

async function runCommand(row: CommandRow, tag: Tag, value: number | string, details: object) {
  row.status = 'executing';
  fire('command', { id: row.id, status: row.status });
  await new Promise((r) => setTimeout(r, 350 + Math.random() * 300));

  const finish = (status: CommandRow['status'], result: string) => {
    Object.assign(row, { status, result, finishedAt: new Date().toISOString() });
    addAudit('command', `tag:${tag.id}`, { command: row.id, tag: tag.name, value, status, result, commandClass: row.commandClass, comment: row.comment, confirmedBy: row.confirmedBy, ...details });
    fire('command', { id: row.id, status, result });
    return { id: row.id, status, result };
  };

  if (!deviceOnline(tag.deviceId)) return finish('rejected', 'нет связи с устройством');
  const pumpCmd = tag.name === 'KNS1_P1_Cmd' || tag.name === 'KNS1_P2_Cmd';
  if (pumpCmd && mem.auto) return finish('rejected', 'КНС в режиме «Автомат» — ПЛК не принимает ручные команды насосам. Переведите в «Ручной».');
  if (tag.name === 'KNS1_P1_Cmd' && scenarios.p1Fault && Number(value)) return finish('failed', 'записано, контрольное чтение: насос не запустился (авария Н-1)');
  if (tag.name === 'KNS1_P1_Unblock') {
    // деблокировка: ПЛК снимает защиту, если причина устранена
    if (row.commandClass === 'critical' && !row.confirmedBy) return finish('rejected', 'критичная команда без второго подтверждения не исполняется');
    scenarios.p1Fault = false; scenarios.overheat = false;
    tick();
    mark('criticalDone');
    return finish('done', 'записано, защита Н-1 деблокирована');
  }

  const map: Record<string, string> = { KNS1_Level_SP: 'sp', KNS1_P1_Cmd: 'p1', KNS1_P2_Cmd: 'p2', KNS1_Valve1_Cmd: 'v1', KNS1_Auto: 'auto' };
  if (map[tag.name]) mem[map[tag.name]] = tag.dataType === 'bool' ? (Number(value) ? 1 : 0) : Number(value);
  else if (tag.address.startsWith('memory:')) mem['m_' + tag.address.slice(7).split('=')[0]] = value as number | string;
  else return finish('done', 'записано (демо: адрес без модели, значение не изменится)');
  tick();
  row.readbackNum = typeof value === 'number' ? (tag.dataType === 'bool' ? (Number(value) ? 1 : 0) : value) : null;
  return finish('done', 'записано, подтверждено чтением');
}

/** Второе подтверждение: другой пользователь, другой сеанс, право «Критичные команды» на область тега. */
export function confirmCommand(id: number, login: string, approve: boolean, comment?: string) {
  const p = awaiting.get(id) ?? bad('Команда уже не ожидает подтверждения (истёк срок или её подтвердили)', 409);
  if (p.expires <= Date.now()) bad('Срок подтверждения истёк', 409);
  if (login.toLowerCase() === p.row.requestedBy.toLowerCase() || sessionOf(login) === p.session) bad('Подтверждать должен другой пользователь со своего рабочего места', 403);
  const area = rtTagArea(p.tag.id);
  if (!has('cmd.critical', area, login)) bad(`Нет права «Критичные команды» на область «${areaPathOf(area)}»`, 403);
  awaiting.delete(id);
  const prev = currentLogin;
  currentLogin = login;
  addAudit('command_confirm', `command:${id}`, { approve, author: p.row.requestedBy, tag: p.tag.name, value: p.value, comment });
  currentLogin = prev;
  if (!approve) {
    Object.assign(p.row, { status: 'rejected', result: `отклонена вторым пользователем ${login}${comment ? `: ${comment}` : ''}`, finishedAt: new Date().toISOString() });
    fire('command', { id, status: p.row.status, result: p.row.result });
    return;
  }
  p.row.confirmedBy = login;
  p.row.expiresAt = new Date(Date.now() + CMD_TTL * 1000).toISOString();
  void runCommand(p.row, p.tag, p.value, { confirmation: 'password+comment+second_user' });
}

/** Для панели прототипа: запросы, ожидающие второго подтверждения. */
export const awaitingCommands = () => [...awaiting.values()].map((p) => ({
  id: p.row.id, tagName: p.tag.name, value: p.value, unit: p.tag.unit, requestedBy: p.row.requestedBy, comment: p.row.comment,
  secondsLeft: Math.max(0, Math.ceil((p.expires - Date.now()) / 1000)),
}));

function expireAwaiting() {
  for (const [id, p] of awaiting) {
    if (p.expires > Date.now()) continue;
    awaiting.delete(id);
    Object.assign(p.row, { status: 'expired', result: 'не подтверждена вторым пользователем в отведённое время', finishedAt: new Date().toISOString() });
    fire('command', { id, status: 'expired', result: p.row.result });
  }
}

function alarmAction(action: 'ack' | 'shelve' | 'unshelve', body: { ids: number[]; comment?: string; minutes?: number }) {
  const perm: Perm = action === 'ack' ? 'alarm.ack' : 'alarm.shelve';
  need(perm);
  if (!body.ids?.length) bad('Не выбраны тревоги');
  const foreign = body.ids.filter((id) => !has(perm, rtAlarmArea(id)));
  if (foreign.length) { addAudit('access_denied', 'alarms:' + foreign.join(','), { perm, action }); bad(`Нет права «${PERM_NAME[perm]}» на часть выбранных тревог (${foreign.length})`, 403); }
  if (action === 'shelve' && !body.comment?.trim()) bad('Для снятия с контроля обязателен комментарий');
  const now = Date.now();
  for (const id of body.ids) {
    const def = rt.alarmDefs.find((d) => d.id === id);
    const st = alarmState.get(id);
    if (!def || !st) continue;
    if (action === 'ack' && !st.acked) {
      Object.assign(st, { acked: true, ackedAt: new Date(now).toISOString(), ackedBy: me()!.login });
      if (body.comment) st.comment = body.comment;
      logAlarm(def, 'ack', st.value, me()!.login, body.comment ?? null);
    } else if (action === 'shelve') {
      Object.assign(st, { shelvedUntil: now + Math.min(1440, Math.max(1, body.minutes ?? 60)) * 60000, active: false, acked: true, comment: body.comment, pending: null });
      logAlarm(def, 'shelve', st.value, me()!.login, body.comment ?? null);
      if (rt.tags.find((t) => t.id === def.tagId)?.name === 'KNS1_Door') mark('shelveDoor');
    } else if (action === 'unshelve' && st.shelvedUntil) {
      st.shelvedUntil = null;
      logAlarm(def, 'unshelve', st.value, me()!.login);
    }
  }
  addAudit('alarm_' + action, 'alarms:' + body.ids.join(','), { comment: body.comment, minutes: body.minutes });
  if (action === 'ack' && myAlarms().every((a) => a.acked)) mark('ackAll');
  fire('alarm', {});
}

/** Тревоги, видимые текущему пользователю (по областям). */
const myAlarms = () => activeAlarms().filter((a) => visible('view', rtTagArea(a.tagId) || ROOT));

function crud<T extends { id: number }>(list: T[], id: number) {
  const i = list.findIndex((x) => x.id === id);
  if (i < 0) bad('Не найдено', 404);
  return i;
}

const OBJ: Record<string, ObjectType> = { device: 'device', tag: 'tag', alarm_def: 'alarm', mnemo: 'mnemo' };

/** Правка черновика: журнал авторства, аудит, уведомление других инженеров. */
function configChanged(table: string, object: string, details: { op: string; name?: string }) {
  const [kind, idStr] = object.split(':');
  const type = OBJ[kind];
  if (type) {
    const op = details.op === 'create' ? 'create' : details.op === 'delete' ? 'delete' : 'update';
    cfg.draftLog.push({ ts: new Date().toISOString(), user: currentLogin ?? '?', type, id: Number(idStr) || 0, name: details.name ?? object, op });
  }
  saveConfig();
  addAudit('config_change', object, details);
  fire('config', table);
}

/** PUT с оптимистической блокировкой: rowVersion из формы должен совпасть с текущим. */
function putVersioned<T extends { id: number; rowVersion?: number }>(list: T[], i: number, body: T) {
  const cur = list[i];
  if (body.rowVersion && (cur.rowVersion ?? 1) !== body.rowVersion)
    bad('Объект уже изменил другой пользователь. Обновите страницу и повторите правку.', 409);
  list[i] = { ...strip(body), id: cur.id, rowVersion: (cur.rowVersion ?? 1) + 1 };
}

function uniqueName<T extends { id: number; name?: string; login?: string }>(list: T[], value: string, id?: number, key: 'name' | 'login' = 'name') {
  if (!value?.trim()) bad('Укажите имя');
  if (list.some((x) => x[key] === value.trim() && x.id !== id)) bad('Объект с таким именем уже существует', 409);
}

/** Публикация требует права «Публикация» на все области, затронутые черновиком. */
function changeAreas(c: ChangeItem, prev: Snapshot, next: Snapshot): number[] {
  const inS = (s: Snapshot): number[] => {
    switch (c.objectType) {
      case 'device': return s.devices.filter((d) => d.id === c.id).map((d) => d.areaId);
      case 'tag': return s.tags.filter((t) => t.id === c.id).map((t) => snapTagArea(s, t));
      case 'alarm': return s.alarmDefs.filter((a) => a.id === c.id).flatMap((a) => s.tags.filter((t) => t.id === a.tagId)).map((t) => snapTagArea(s, t));
      default: return s.mnemos.filter((m) => m.id === c.id).map((m) => m.areaId);
    }
  };
  const extra = c.objectType === 'device'
    ? [...prev.tags, ...next.tags].filter((t) => t.deviceId === c.id && t.areaId === null).flatMap((t) => [snapTagArea(prev, t), snapTagArea(next, t)]) : [];
  return [...new Set([...inS(prev), ...inS(next), ...extra].map((a) => a || ROOT))];
}
function checkPublishAreas(prev: Snapshot, next: Snapshot, action: string) {
  const foreign = diff(prev, next).map((c) => ({ c, areas: changeAreas(c, prev, next).filter((a) => !has('config.publish', a)) }))
    .filter((x) => x.areas.length).map((x) => ({ objectType: x.c.objectType, id: x.c.id, name: x.c.name, areas: x.areas.map(areaPathOf) }));
  if (!foreign.length) return;
  addAudit('publish_denied', 'config', { reason: 'areas', action, foreign });
  bad(`Нет права «Публикация» на области изменений (${foreign.length}): ${foreign.slice(0, 3).map((f) => `${f.name} — ${f.areas.join(', ')}`).join('; ')}${foreign.length > 3 ? '…' : ''}. Их должен опубликовать инженер с нужными правами.`, 403, { foreign });
}

function areaDto(a: Area) {
  return { ...a, path: areaPathOf(a.id),
    devices: cfg.devices.filter((d) => d.areaId === a.id).length,
    tags: cfg.tags.filter((t) => draftTagArea(t) === a.id).length,
    mnemos: cfg.mnemos.filter((m) => !m.deleted && m.areaId === a.id).length };
}

function assignmentDto(a: DemoAssignment) {
  return { ...a, roleName: cfg.roles.find((r) => r.id === a.roleId)?.name ?? '?', areaPath: areaPathOf(a.areaId),
    expired: !!a.validUntil && Date.parse(a.validUntil) <= Date.now() };
}

function validateAssignment(b: DemoAssignment) {
  if (!['user', 'group'].includes(b.principalType)) bad('Укажите, кому выдаётся роль');
  if (!String(b.principal ?? '').trim()) bad(b.principalType === 'user' ? 'Укажите логин' : 'Укажите имя группы AD');
  const role = cfg.roles.find((r) => r.id === b.roleId) ?? bad('Роль не найдена');
  if (!cfg.areas.some((a) => a.id === b.areaId)) bad('Область не найдена');
  const rootOnly = role.permissions.filter((p) => ROOT_ONLY.has(p));
  if (rootOnly.length && b.areaId !== ROOT) bad(`Права «${rootOnly.map((p) => PERM_NAME[p]).join('», «')}» действуют только на корневой области «${areaPathOf(ROOT)}»`);
  if (b.validUntil && Date.parse(b.validUntil) <= Date.now()) bad('Срок действия уже прошёл');
  if (b.principalType === 'user' && !cfg.users.some((u) => u.login.toLowerCase() === b.principal.trim().toLowerCase())) bad(`Пользователь «${b.principal}» не найден`);
}

/** Нельзя лишить права «Пользователи» самого себя. */
function selfLockout(apply: () => void) {
  const backup = structuredClone({ roles: cfg.roles, assignments: cfg.assignments });
  apply();
  if (!has('users.manage', ROOT)) {
    cfg.roles = backup.roles; cfg.assignments = backup.assignments;
    bad('Нельзя лишить права «Пользователи» самого себя — попросите другого администратора безопасности');
  }
}

async function route(method: string, path: string, q: URLSearchParams, body: any): Promise<unknown> {
  // --- авторизация
  if (path === '/api/auth/me') { const m = meDto(); if (!m) bad('Не выполнен вход', 401); return m; }
  if (path === '/api/auth/login') {
    const login = String(body.login ?? '').trim().toLowerCase();
    const u = cfg.users.find((x) => x.login.toLowerCase() === login);
    if (!u || !u.enabled) { audit.unshift({ id: auditSeq++, ts: new Date().toISOString(), user: login, action: 'login_failed', object: `user:${login}`, details: {}, ip: '10.0.5.17' }); bad(`Неверный логин или пароль. В демо: ${personas.map((p) => p.login).join(', ')}, пароль любой`, 401); }
    currentLogin = u.login;
    u.lastLoginAt = new Date().toISOString();
    if (u.emergency) {
      addAudit('emergency_login', `user:${u.login}`, { note: 'вход аварийной локальной учётной записью' });
      setTimeout(() => fire('systemNotice', { level: 'warning', text: `Вход аварийной локальной учётной записью «${u.login}» с 10.0.5.17` }), 1500);
    } else addAudit('login', `user:${u.login}`);
    return meDto();
  }
  if (path === '/api/auth/logout') { addAudit('logout', `user:${currentLogin}`); currentLogin = null; return undefined; }
  if (!me()) bad('Не выполнен вход', 401);
  if (path === '/api/auth/change-password') { addAudit('password_change', `user:${currentLogin}`); return undefined; }
  if (path === '/api/permissions') return PERMS.map((p) => ({ code: p.code, name: p.name, rootOnly: !!p.rootOnly }));
  if (path === '/api/areas' && method === 'GET') return cfg.areas.map(areaDto);
  if (path === '/api/commands/settings') return { ttlSeconds: CMD_TTL, confirmSeconds: CONFIRM_S, criticalConfirmMode: 'second_user' };

  // --- области (сразу, без черновика)
  if (path === '/api/areas' && method === 'POST') {
    need('config.edit', 'users.manage');
    const parent = body.parentId ?? ROOT;
    if (!has('users.manage', parent)) denyUnless('config.edit', parent, 'area:new');
    if (!String(body.name ?? '').trim()) bad('Укажите название области');
    if (cfg.areas.some((a) => a.parentId === parent && a.name === body.name.trim())) bad('Область с таким названием уже есть', 409);
    const a: Area = { id: nextId(), parentId: parent, name: body.name.trim(), sortOrder: body.sortOrder ?? 0, path: '' };
    cfg.areas.push(a); saveConfig(); addAudit('area_change', `area:${a.id}`, { op: 'create', path: areaPathOf(a.id) });
    return { id: a.id };
  }
  let m = path.match(/^\/api\/areas\/(\d+)$/);
  if (m) {
    need('config.edit', 'users.manage');
    const id = Number(m[1]); const i = crud(cfg.areas, id); const a = cfg.areas[i];
    if (!has('users.manage', id)) denyUnless('config.edit', id, `area:${id}`);
    if (method === 'PUT') {
      const parent = a.parentId === null ? null : body.parentId ?? a.parentId;
      if (parent !== null && within(parent, id)) bad('Нельзя перенести область внутрь самой себя');
      const before = areaPathOf(id);
      Object.assign(a, { name: String(body.name).trim(), parentId: parent, sortOrder: body.sortOrder ?? 0 });
      saveConfig(); addAudit('area_change', `area:${id}`, { op: 'update', before, after: areaPathOf(id) }); return undefined;
    }
    if (method === 'DELETE') {
      if (a.parentId === null) bad('Корневую область удалить нельзя');
      if (cfg.areas.some((x) => x.parentId === id)) bad('В области есть вложенные области', 409);
      const objs = cfg.devices.filter((d) => d.areaId === id).length + cfg.tags.filter((t) => t.areaId === id).length + cfg.mnemos.filter((x) => !x.deleted && x.areaId === id).length;
      if (objs) bad(`В области есть объекты черновика (${objs}) — перенесите их`, 409);
      if (cfg.assignments.some((x) => x.areaId === id)) bad('На область выданы назначения — удалите их', 409);
      const path0 = areaPathOf(id);
      cfg.areas.splice(i, 1); saveConfig(); addAudit('area_change', `area:${id}`, { op: 'delete', path: path0 }); return undefined;
    }
  }

  // --- роли
  if (path === '/api/roles' && method === 'GET') { need('users.manage'); return cfg.roles.map((r) => ({ ...r, assignments: cfg.assignments.filter((a) => a.roleId === r.id).length })); }
  if (path === '/api/roles' && method === 'POST') {
    need('users.manage'); if (!String(body.name ?? '').trim()) bad('Укажите название роли');
    if (cfg.roles.some((r) => r.name === body.name.trim())) bad('Роль с таким названием уже есть', 409);
    const r: DemoRole = { id: nextId(), code: null, name: body.name.trim(), description: body.description ?? '', isSystem: false, permissions: PERMS.map((p) => p.code).filter((c) => body.permissions?.includes(c)) };
    cfg.roles.push(r); saveConfig(); addAudit('role_change', `role:${r.id}`, { op: 'create', name: r.name, after: r.permissions }); mark('roleCreated'); return { id: r.id };
  }
  m = path.match(/^\/api\/roles\/(\d+)$/);
  if (m) {
    need('users.manage'); const id = Number(m[1]); const i = crud(cfg.roles, id); const r = cfg.roles[i];
    if (r.isSystem) bad(method === 'DELETE' ? 'Системную роль удалить нельзя' : 'Системную роль изменить нельзя — скопируйте её в свою и измените копию');
    if (method === 'PUT') {
      const before = r.permissions;
      selfLockout(() => Object.assign(r, { name: body.name.trim(), description: body.description ?? '', permissions: PERMS.map((p) => p.code).filter((c) => body.permissions?.includes(c)) }));
      saveConfig(); addAudit('role_change', `role:${id}`, { op: 'update', before, after: r.permissions }); return undefined;
    }
    if (method === 'DELETE') {
      if (cfg.assignments.some((a) => a.roleId === id)) bad('Роль выдана в назначениях. Сначала удалите их.', 409);
      cfg.roles.splice(i, 1); saveConfig(); addAudit('role_change', `role:${id}`, { op: 'delete', name: r.name }); return undefined;
    }
  }

  // --- назначения
  if ((path === '/api/assignments' || path === '/api/ad-groups') && method === 'GET') {
    need('users.manage');
    const type = path === '/api/ad-groups' ? 'group' : q.get('principalType'), who = q.get('principal');
    return cfg.assignments.filter((a) => (!type || a.principalType === type) && (!who || a.principal.toLowerCase() === who.toLowerCase())).map(assignmentDto);
  }
  if (path === '/api/assignments' && method === 'POST') {
    need('users.manage'); validateAssignment(body);
    if (cfg.assignments.some((a) => a.principalType === body.principalType && a.principal === body.principal.trim() && a.roleId === body.roleId && a.areaId === body.areaId)) bad('Такое назначение уже есть', 409);
    const a: DemoAssignment = { id: nextId(), principalType: body.principalType, principal: body.principal.trim(), roleId: body.roleId, areaId: body.areaId,
      validUntil: body.validUntil ?? null, comment: body.comment ?? '', createdAt: new Date().toISOString(), createdBy: currentLogin };
    cfg.assignments.push(a); saveConfig();
    addAudit('assignment_change', `assignment:${a.id}`, { op: 'create', after: assignmentDto(a), permissions: cfg.roles.find((r) => r.id === a.roleId)?.permissions });
    if (a.principal === 'pnr.kuznetsov' && a.roleId === 2 && a.areaId === 4 && a.validUntil) mark('assign');
    fire('config', 'access');
    return { id: a.id };
  }
  m = path.match(/^\/api\/assignments\/(\d+)$/);
  if (m) {
    need('users.manage'); const id = Number(m[1]); const i = crud(cfg.assignments, id); const before = assignmentDto(cfg.assignments[i]);
    if (method === 'PUT') {
      validateAssignment(body);
      selfLockout(() => Object.assign(cfg.assignments[i], { principalType: body.principalType, principal: body.principal.trim(), roleId: body.roleId, areaId: body.areaId, validUntil: body.validUntil ?? null, comment: body.comment ?? '' }));
      saveConfig(); addAudit('assignment_change', `assignment:${id}`, { op: 'update', before, after: assignmentDto(cfg.assignments[i]) });
      const a = cfg.assignments[i];
      if (a.principal === 'pnr.kuznetsov' && a.roleId === 2 && a.areaId === 4 && a.validUntil) mark('assign');
      return undefined;
    }
    if (method === 'DELETE') {
      selfLockout(() => cfg.assignments.splice(i, 1));
      saveConfig(); addAudit('assignment_change', `assignment:${id}`, { op: 'delete', before }); return undefined;
    }
  }
  if (path === '/api/directory/status') { need('users.manage'); return { enabled: false, lastOkAt: null, lastErrorAt: null, lastError: null }; }
  if (path === '/api/directory/test') bad('Интеграция с AD выключена (в прототипе домена нет)');

  // --- устройства
  if (path === '/api/devices' && method === 'GET') { need('view'); return devicesDto(); }
  if (path === '/api/devices' && method === 'POST') {
    need('config.edit'); denyUnless('config.edit', body.areaId ?? ROOT, 'device:new'); uniqueName(cfg.devices, body.name);
    const d = { ...strip(body), id: nextId(), name: body.name.trim(), rowVersion: 1, areaId: body.areaId ?? ROOT } as Device;
    cfg.devices.push(d); configChanged('devices', `device:${d.id}`, { op: 'create', name: d.name }); return { id: d.id };
  }
  m = path.match(/^\/api\/devices\/(\d+)$/);
  if (m) {
    need('config.edit'); const id = Number(m[1]); const i = crud(cfg.devices, id);
    denyUnless('config.edit', cfg.devices[i].areaId, `device:${id}`);
    if (method === 'PUT') {
      denyUnless('config.edit', body.areaId ?? ROOT, `device:${id}`);
      uniqueName(cfg.devices, body.name, id); putVersioned(cfg.devices, i, body); configChanged('devices', `device:${id}`, { op: 'update', name: body.name }); return undefined;
    }
    if (method === 'DELETE') {
      if (cfg.tags.some((t) => t.deviceId === id)) bad('Есть связанные объекты: сначала удалите теги устройства', 409);
      const [d] = cfg.devices.splice(i, 1); configChanged('devices', `device:${id}`, { op: 'delete', name: d.name }); return undefined;
    }
  }

  // --- теги
  if (path === '/api/tags' && method === 'GET') {
    need('view');
    const dev = q.get('deviceId');
    return cfg.tags.filter((t) => (!dev || t.deviceId === Number(dev)) && visible('view', draftTagArea(t))).sort((a, b) => a.name.localeCompare(b.name)).map(tagDto);
  }
  if (path === '/api/tags' && method === 'POST') {
    need('config.edit');
    denyUnless('config.edit', body.areaId ?? cfg.devices.find((d) => d.id === body.deviceId)?.areaId ?? ROOT, 'tag:new');
    uniqueName(cfg.tags, body.name);
    if (!body.address?.trim()) bad('Недопустимое значение: адрес не задан');
    const t = { ...strip(body), id: nextId(), name: body.name.trim(), rowVersion: 1, areaId: body.areaId ?? null, commandClass: body.commandClass ?? 'normal' } as Tag;
    cfg.tags.push(t); configChanged('tags', `tag:${t.id}`, { op: 'create', name: t.name }); return { id: t.id };
  }
  if (path === '/api/tags/import' && method === 'POST') {
    need('config.edit');
    return importCsv(String(body ?? ''));
  }
  m = path.match(/^\/api\/tags\/(\d+)$/);
  if (m) {
    const id = Number(m[1]); const i = crud(cfg.tags, id);
    if (method === 'GET') { if (!visible('view', draftTagArea(cfg.tags[i]))) bad('Не найдено', 404); return tagDto(cfg.tags[i]); }
    need('config.edit');
    denyUnless('config.edit', draftTagArea(cfg.tags[i]), `tag:${id}`);
    if (method === 'PUT') {
      denyUnless('config.edit', body.areaId ?? cfg.devices.find((d) => d.id === body.deviceId)?.areaId ?? ROOT, `tag:${id}`);
      uniqueName(cfg.tags, body.name, id); putVersioned(cfg.tags, i, body); configChanged('tags', `tag:${id}`, { op: 'update', name: body.name }); return undefined;
    }
    if (method === 'DELETE') {
      if (cfg.alarmDefs.some((a) => a.tagId === id)) bad('Есть связанные объекты: у тега настроены тревоги', 409);
      const [t] = cfg.tags.splice(i, 1); configChanged('tags', `tag:${id}`, { op: 'delete', name: t.name }); return undefined;
    }
  }

  // --- уставки тревог
  if (path === '/api/alarm-defs' && method === 'GET') {
    need('view');
    const tagId = q.get('tagId');
    return cfg.alarmDefs.filter((a) => (!tagId || a.tagId === Number(tagId)) && visible('view', draftTagAreaById(a.tagId)));
  }
  if (path === '/api/alarm-defs' && method === 'POST') {
    need('config.edit'); denyUnless('config.edit', draftTagAreaById(body.tagId), 'alarm_def:new');
    const a = { ...body, id: nextId(), rowVersion: 1 } as AlarmDef;
    cfg.alarmDefs.push(a); configChanged('alarm_defs', `alarm_def:${a.id}`, { op: 'create', name: a.message }); return { id: a.id };
  }
  m = path.match(/^\/api\/alarm-defs\/(\d+)$/);
  if (m) {
    need('config.edit'); const id = Number(m[1]); const i = crud(cfg.alarmDefs, id);
    denyUnless('config.edit', draftTagAreaById(cfg.alarmDefs[i].tagId), `alarm_def:${id}`);
    if (method === 'PUT') { denyUnless('config.edit', draftTagAreaById(body.tagId), `alarm_def:${id}`); putVersioned(cfg.alarmDefs, i, body); configChanged('alarm_defs', `alarm_def:${id}`, { op: 'update', name: body.message }); return undefined; }
    if (method === 'DELETE') { const [a] = cfg.alarmDefs.splice(i, 1); configChanged('alarm_defs', `alarm_def:${id}`, { op: 'delete', name: a.message }); return undefined; }
  }

  // --- тревоги
  if (path === '/api/alarms/active') { need('view'); return myAlarms(); }
  if (path === '/api/alarms/events') {
    need('view');
    const from = Date.parse(q.get('from') ?? '') || Date.now() - 86400000, to = Date.parse(q.get('to') ?? '') || Date.now();
    return alarmEvents.filter((e) => { const t = Date.parse(e.ts); return t >= from && t <= to && visible('view', rtAlarmArea(e.alarmId) || ROOT); }).slice(0, Number(q.get('limit') ?? 500));
  }
  if (path === '/api/alarms/ack') { alarmAction('ack', body); return undefined; }
  if (path === '/api/alarms/shelve') { alarmAction('shelve', body); return undefined; }
  if (path === '/api/alarms/unshelve') { alarmAction('unshelve', body); return undefined; }

  // --- команды
  if (path === '/api/commands' && method === 'POST') { need('cmd.control', 'cmd.setpoint', 'cmd.critical'); return executeCommand(body); }
  if (path === '/api/commands/awaiting') {
    need('cmd.critical');
    return [...awaiting.values()].filter((p) => p.expires > Date.now() && p.row.requestedBy !== currentLogin && has('cmd.critical', rtTagArea(p.tag.id)))
      .map((p) => ({ id: p.row.id, tagId: p.tag.id, tagName: p.tag.name, tagDescription: p.tag.description, unit: p.tag.unit, valueNum: p.row.valueNum,
        valueStr: p.row.valueStr, currentValue: current.get(p.tag.id)?.[1] ?? null, requestedBy: p.row.requestedBy, requestedAt: p.row.requestedAt,
        expiresAt: new Date(p.expires).toISOString(), comment: p.row.comment, areaPath: areaPathOf(rtTagArea(p.tag.id)) }));
  }
  m = path.match(/^\/api\/commands\/(\d+)\/confirm$/);
  if (m) {
    need('cmd.critical');
    if (!String(body.password ?? '')) bad('Неверный пароль', 403);
    confirmCommand(Number(m[1]), currentLogin!, body.approve !== false, body.comment);
    return undefined;
  }
  if (path === '/api/commands') { need('view'); return commands.filter((c) => visible('view', rtTagArea(c.tagId) || ROOT)).slice(0, Number(q.get('limit') ?? 100)); }

  // --- архив
  if (path === '/api/history') {
    need('view');
    const ids = (q.get('tagIds') ?? '').split(',').map(Number).filter((x) => x > 0 && visible('view', rtTagArea(x) || ROOT));
    if (!ids.length || ids.length > 16) bad('Укажите от 1 до 16 тегов');
    const from = Date.parse(q.get('from') ?? ''), to = Date.parse(q.get('to') ?? '');
    if (!(to > from)) bad('Неверный интервал');
    const names = ids.map((id) => rt.tags.find((t) => t.id === id)?.name);
    if (names.includes('KNS1_Level') && names.includes('KNS1_Pressure') && to - from >= 50 * 60_000) mark('trend');
    return historyQuery(ids, from, to, Math.min(5000, Math.max(50, Number(q.get('points') ?? 1000))));
  }

  // --- мнемосхемы: операторам — опубликованная версия, редактору (?draft=1) — черновик; чужие области не показываются
  if (path === '/api/mnemos' && method === 'GET') {
    need('view');
    const order = (a: { parentId: number | null; sortOrder: number; name: string }, b: typeof a) =>
      Number(a.parentId !== null) - Number(b.parentId !== null) || a.sortOrder - b.sortOrder || a.name.localeCompare(b.name);
    if (q.get('draft')) { need('mnemo.edit'); return cfg.mnemos.filter((x) => !x.deleted && visible('view', x.areaId)).sort(order).map(mnemoDto); }
    return rt.mnemos.filter((r) => visible('view', r.areaId || ROOT)).sort(order).map((r) => {
      const v = cfg.mnemos.find((x) => x.id === r.id)?.versions.find((x) => x.version === r.version);
      return { ...r, currentVersion: r.version, publishedVersion: r.version, updatedAt: v?.createdAt ?? published().createdAt, updatedBy: v?.createdBy ?? null };
    });
  }
  if (path === '/api/mnemos' && method === 'POST') {
    need('mnemo.edit'); if (!body.name?.trim()) bad('Укажите название');
    const area = body.areaId ?? (body.parentId ? cfg.mnemos.find((x) => x.id === body.parentId)?.areaId : undefined) ?? ROOT;
    denyUnless('mnemo.edit', area, 'mnemo:new');
    const at = new Date().toISOString();
    const rec: MnemoRec = { id: nextId(), name: body.name.trim(), parentId: body.parentId ?? null, sortOrder: body.sortOrder ?? 0, areaId: area, versions: [], updatedAt: at, updatedBy: currentLogin! };
    cfg.mnemos.push(rec); configChanged('mnemos', `mnemo:${rec.id}`, { op: 'create', name: rec.name }); return { id: rec.id };
  }
  m = path.match(/^\/api\/mnemos\/(\d+)(\/doc|\/versions)?$/);
  if (m) {
    need('view');
    const id = Number(m[1]); const i = crud(cfg.mnemos, id); const rec = cfg.mnemos[i];
    const pubRef = rt.mnemos.find((x) => x.id === id);
    if (!visible('view', (q.get('draft') || !pubRef ? rec.areaId : pubRef.areaId) || ROOT)) bad('Не найдено', 404);
    if (m[2] === '/versions') return rec.versions.slice().reverse().map((v) => ({ version: v.version, createdAt: v.createdAt, createdBy: v.createdBy }));
    if (method === 'GET') {
      if (id === 2) mark('kns2');
      let ver: MnemoRec['versions'][number] | undefined;
      if (q.get('version')) ver = rec.versions.find((v) => v.version === Number(q.get('version')));
      else if (q.get('draft')) { if (rec.deleted) bad('Не найдено', 404); ver = rec.versions.at(-1); }
      else {
        const pub = pubRef ?? bad('Схема ещё не опубликована', 404);
        ver = rec.versions.find((v) => v.version === pub.version);
        return { id, name: pub.name, version: pub.version, doc: ver?.doc ?? null, areaId: pub.areaId };
      }
      return { id, name: rec.name, version: ver?.version ?? 0, doc: ver?.doc ?? null, areaId: rec.areaId };
    }
    need('mnemo.edit');
    if (rec.deleted) bad('Не найдено', 404);
    denyUnless('mnemo.edit', rec.areaId, `mnemo:${id}`);
    if (m[2] === '/doc' && method === 'PUT') {
      const cur = rec.versions.at(-1)?.version ?? 0;
      if (cur !== body.baseVersion) bad(`Схему уже изменили (версия ${cur}). Обновите редактор и повторите.`, 409);
      const at = new Date().toISOString();
      rec.versions.push({ version: cur + 1, doc: body.doc, createdAt: at, createdBy: currentLogin! });
      rec.updatedAt = at; rec.updatedBy = currentLogin!;
      configChanged('mnemos', `mnemo:${id}`, { op: 'save', name: rec.name });
      return { version: cur + 1 };
    }
    if (method === 'PUT') {
      const area = body.areaId ?? rec.areaId;
      denyUnless('mnemo.edit', area, `mnemo:${id}`);
      rec.name = body.name.trim(); rec.parentId = body.parentId ?? null; rec.sortOrder = body.sortOrder ?? 0; rec.areaId = area;
      configChanged('mnemos', `mnemo:${id}`, { op: 'rename', name: rec.name }); return undefined;
    }
    if (method === 'DELETE') { rec.deleted = true; cfg.mnemos.forEach((x) => { if (x.parentId === id) x.parentId = null; }); configChanged('mnemos', `mnemo:${id}`, { op: 'delete', name: rec.name }); return undefined; }
  }

  // --- черновик и публикация
  if (path === '/api/draft/status') {
    const changes = diff(rt, draftSnapshot());
    const v = published();
    return { publishedVersion: v.version, publishedAt: v.createdAt, publishedBy: v.createdBy, appliedAt: v.appliedAt,
      draftChanges: changes.length, draftAuthors: [...new Set(cfg.draftLog.map((l) => l.user))] };
  }
  if (path === '/api/draft/diff' || path === '/api/draft/validate') {
    need('config.edit', 'mnemo.edit', 'config.publish');
    const next = draftSnapshot();
    const changes = diff(rt, next);
    const { errors, warnings } = validate(next, rt);
    return { baseVersion: published().version, summary: summarize(changes), errors, warnings,
      changes: changes.map((c) => {
        const log = cfg.draftLog.filter((l) => l.type === c.objectType && l.id === c.id);
        const areas = changeAreas(c, rt, next);
        return { change: c, authors: [...new Set(log.map((l) => l.user))], lastAt: log.at(-1)?.ts ?? null,
          areas: areas.map(areaPathOf), canPublish: areas.every((a) => has('config.publish', a)) };
      }) };
  }
  if (path === '/api/draft/discard' && method === 'POST') {
    need('config.publish'); checkPassword(body.password, 'draft_discard');
    checkPublishAreas(rt, draftSnapshot(), 'discard');
    const n = diff(rt, draftSnapshot()).length;
    restore(rt);
    addAudit('draft_discard', 'config', { discarded: n });
    saveConfig(); fire('config', 'all');
    return { discarded: n };
  }
  if (path === '/api/publish' && method === 'POST') {
    need('config.publish'); checkPassword(body.password, 'config_publish');
    if (body.baseVersion !== published().version) bad(`Пока вы проверяли изменения, опубликована версия ${published().version}. Список изменений обновлён — проверьте его ещё раз.`, 409);
    if (!String(body.comment ?? '').trim()) bad('Укажите причину изменения');
    const next = draftSnapshot();
    if (!diff(rt, next).length) bad('Нет изменений для публикации');
    checkPublishAreas(rt, next, 'publish');
    const { errors, warnings } = validate(next, rt);
    if (errors.length) bad(`Публикация невозможна: ошибок — ${errors.length}`, 422, { errors, warnings });
    if (warnings.length && !body.acceptWarnings) bad(`Подтвердите, что ознакомлены с предупреждениями (${warnings.length})`, 428, { errors, warnings });
    return { version: publishSnapshot(next, String(body.comment).trim(), warnings.length, null) };
  }
  if (path === '/api/versions' && method === 'GET') { need('config.edit', 'mnemo.edit', 'config.publish'); return cfg.versions.slice().reverse().map(versionDto); }
  m = path.match(/^\/api\/versions\/(\d+)(\/rollback)?$/);
  if (m) {
    need('config.edit', 'mnemo.edit', 'config.publish');
    const target = cfg.versions.find((v) => v.version === Number(m![1])) ?? bad('Версия не найдена', 404);
    if (!m[2]) return { ...versionDto(target), changes: target.changes };
    need('config.publish');
    checkPassword(body.password, 'config_rollback');
    if (!String(body.comment ?? '').trim()) bad('Укажите причину отката');
    if (target.version === published().version) bad('Эта версия уже работает на объекте');
    if (diff(rt, draftSnapshot()).length && !body.discardDraft) bad('В черновике есть неопубликованные правки. Отметьте «Сбросить черновик» или сначала опубликуйте их.', 409);
    checkPublishAreas(rt, target.snapshot, 'rollback');
    const { errors, warnings } = validate(target.snapshot, rt);
    if (errors.length) bad(`Откат невозможен: ошибок — ${errors.length}`, 422, { errors, warnings });
    if (warnings.length && !body.acceptWarnings) bad(`Подтвердите, что ознакомлены с предупреждениями (${warnings.length})`, 428, { errors, warnings });
    restore(target.snapshot);
    return { version: publishSnapshot(draftSnapshot(), String(body.comment).trim(), warnings.length, target.version) };
  }

  // --- пользователи (права — назначениями)
  if (path === '/api/users' && method === 'GET') {
    need('users.manage');
    return cfg.users.map((u) => ({ ...u, roles: cfg.assignments.filter((a) => a.principalType === 'user' && a.principal === u.login)
      .map((a) => `${cfg.roles.find((r) => r.id === a.roleId)?.name ?? '?'} — ${areaPathOf(a.areaId)}${a.validUntil ? ` (до ${new Date(a.validUntil).toLocaleDateString('ru-RU')})` : ''}`) }));
  }
  if (path === '/api/users' && method === 'POST') {
    need('users.manage'); uniqueName(cfg.users, body.login, undefined, 'login');
    if (String(body.password ?? '').length < 8) bad('Пароль должен быть не короче 8 символов');
    const u: DemoUser = { id: nextId(), login: body.login.trim(), displayName: body.displayName, source: 'local', emergency: !!body.emergency, roles: [],
      enabled: body.enabled, mustChangePassword: true, createdAt: new Date().toISOString(), lastLoginAt: null };
    cfg.users.push(u); saveConfig(); addAudit('user_change', `user:${u.login}`, { op: 'create', emergency: u.emergency }); return { id: u.id };
  }
  m = path.match(/^\/api\/users\/(\d+)$/);
  if (m) {
    need('users.manage'); const id = Number(m[1]); const i = crud(cfg.users, id); const u = cfg.users[i];
    if (method === 'PUT') {
      if (u.login === currentLogin && !body.enabled) bad('Нельзя отключить самого себя');
      if (body.password && String(body.password).length < 8) bad('Пароль должен быть не короче 8 символов');
      Object.assign(u, { displayName: body.displayName, enabled: body.enabled, emergency: !!body.emergency, mustChangePassword: body.password ? true : u.mustChangePassword });
      saveConfig(); addAudit('user_change', `user:${u.login}`, { op: 'update', enabled: u.enabled, emergency: u.emergency, passwordReset: !!body.password }); return undefined;
    }
    if (method === 'DELETE') {
      if (u.login === currentLogin) bad('Нельзя удалить самого себя');
      const removed = cfg.assignments.filter((a) => a.principalType === 'user' && a.principal === u.login).length;
      cfg.assignments = cfg.assignments.filter((a) => !(a.principalType === 'user' && a.principal === u.login));
      cfg.users.splice(i, 1); saveConfig(); addAudit('user_change', `user:${u.login}`, { op: 'delete', assignmentsRemoved: removed }); return undefined;
    }
  }

  // --- аудит и диагностика
  if (path === '/api/audit') {
    need('audit.view');
    const action = q.get('action');
    return audit.filter((a) => !action || a.action === action).slice(0, Number(q.get('limit') ?? 500));
  }
  if (path === '/api/audit/verify') { need('audit.view'); return { intact: true, firstBrokenId: null }; }
  if (path === '/api/diagnostics') {
    need('view');
    mark('kns2');
    return {
      web: { listenerConnected: true, cachedValues: current.size, connections: 3, version: '0.2.0-demo' },
      acquisition: { lastHeartbeatAt: new Date(lastHb || Date.now()).toISOString(), heartbeat: { tags: rt.tags.length, devices: rt.devices.length, historianQueue: 0, spoolBytes: 0 } },
      db: { dbBytes: 412 * 1048576, tags: cfg.tags.length, devices: cfg.devices.length, activeAlarms: activeAlarms().filter((a) => a.active).length, historyPartitions: 4 },
    };
  }
  if (path === '/api/values') { need('view'); return [...current.values()].filter((v) => rt.tags.some((t) => t.id === v[0]) && visible('view', rtTagArea(v[0]) || ROOT)); }
  bad('Не найдено', 404);
}

function importCsv(text: string) {
  const lines = text.replace(/^﻿/, '').split(/\r?\n/).filter((l) => l.trim());
  if (!lines.length) bad('Файл пуст', 400, { errors: ['Файл пуст'] });
  const delim = lines[0].includes(';') ? ';' : ',';
  const head = lines[0].split(delim).map((h) => h.trim().toLowerCase());
  const errors: string[] = [];
  for (const req of ['name', 'device', 'address', 'data_type']) if (!head.includes(req)) errors.push(`Нет обязательной колонки «${req}»`);
  if (errors.length) bad(errors[0], 400, { errors });
  const rows: Tag[] = [];
  lines.slice(1).forEach((line, i) => {
    const cells = line.split(delim);
    const g = (c: string) => (cells[head.indexOf(c)] ?? '').trim();
    const dev = cfg.devices.find((d) => d.name.toLowerCase() === g('device').toLowerCase());
    if (!dev) { errors.push(`Строка ${i + 2}: устройство «${g('device')}» не найдено`); return; }
    if (!['bool', 'int16', 'uint16', 'int32', 'uint32', 'float32', 'float64', 'string'].includes(g('data_type'))) { errors.push(`Строка ${i + 2}: тип «${g('data_type')}» не поддерживается`); return; }
    const num = (c: string) => (g(c) === '' ? null : Number(g(c).replace(',', '.')));
    const existing = cfg.tags.find((t) => t.name === g('name'));
    if (existing && !has('config.edit', draftTagArea(existing))) { errors.push(`Строка ${i + 2}: тег уже есть в области «${areaPathOf(draftTagArea(existing))}», на которую нет права «Конфигурация»`); return; }
    if (!has('config.edit', dev.areaId)) { errors.push(`Строка ${i + 2}: нет права «Конфигурация» на область «${areaPathOf(dev.areaId)}»`); return; }
    rows.push({ ...(existing ?? tagsSeed[0]), id: existing?.id ?? 0, name: g('name'), description: g('description'), deviceId: dev.id, address: g('address'),
      dataType: g('data_type') as Tag['dataType'], unit: g('unit'), rawLow: num('raw_low'), rawHigh: num('raw_high'), engLow: num('eng_low'), engHigh: num('eng_high'),
      writable: ['1', 'true', 'да'].includes(g('writable').toLowerCase()), writeMin: num('write_min'), writeMax: num('write_max') });
  });
  if (errors.length) bad(errors.join('\n'), 400, { errors });
  let inserted = 0, updated = 0;
  for (const r of rows) {
    const i = cfg.tags.findIndex((t) => t.name === r.name);
    if (i >= 0) { cfg.tags[i] = { ...r, id: cfg.tags[i].id, rowVersion: (cfg.tags[i].rowVersion ?? 1) + 1 }; updated++; } else { cfg.tags.push({ ...r, id: nextId(), rowVersion: 1 }); inserted++; }
  }
  configChanged('tags', 'tag:0', { op: 'update', name: `импорт CSV: новых ${inserted}, изменённых ${updated}` });
  return { inserted, updated, errors: [] };
}

export function installMockFetch() {
  const real = window.fetch.bind(window);
  window.fetch = async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = new URL(typeof input === 'string' ? input : input instanceof URL ? input.href : input.url, location.href);
    if (!url.pathname.startsWith('/api/') && url.pathname !== '/health') return real(input, init);
    const method = (init?.method ?? 'GET').toUpperCase();
    let body: unknown = undefined;
    if (typeof init?.body === 'string') {
      try { body = JSON.parse(init.body); } catch { body = init.body; }
    }
    await new Promise((r) => setTimeout(r, 60 + Math.random() * 90)); // сетевая задержка
    try {
      const result = await route(method, url.pathname, url.searchParams, body ?? {});
      return result === undefined ? new Response(null, { status: 204 }) : new Response(JSON.stringify(result), { status: 200, headers: { 'Content-Type': 'application/json' } });
    } catch (e) {
      const err = e instanceof HttpError ? e : new HttpError(500, String(e));
      return new Response(JSON.stringify({ title: err.message, detail: err.message, ...(err.extra ?? {}) }), { status: err.status, headers: { 'Content-Type': 'application/json' } });
    }
  };
}

export function initDemo() {
  prehistory();
  installMockFetch();
  // тревоги и значения уже есть в момент открытия страницы
  const now = Date.now();
  computeValues(now);
  evaluateAlarms(now - 10_000);
  evaluateAlarms(now);
  // правдоподобное время возникновения уже существующих тревог
  const backdate = (tagName: string, ms: number) => {
    const def = rt.alarmDefs.find((d) => rt.tags.find((t) => t.id === d.tagId)?.name === tagName && d.kind !== 'hi' && d.kind !== 'lo' && d.kind !== 'hihi');
    const st = def && alarmState.get(def.id);
    if (!def || !st?.active) return;
    st.raisedAt = new Date(now - ms).toISOString();
    const ev = alarmEvents.find((e) => e.alarmId === def.id && e.event === 'raise');
    if (ev) ev.ts = st.raisedAt;
  };
  backdate('KNS2_Level', (3 * 3600 + 17 * 60) * 1000);
  backdate('KNS1_Door', 26 * 60 * 1000);
  alarmEvents.sort((a, b) => b.ts.localeCompare(a.ts));
  addAudit('login', `user:${currentLogin}`);
}
