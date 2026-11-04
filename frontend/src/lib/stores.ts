import { create } from 'zustand';
import { api } from './api';
import { onRealtime } from './realtime';
import type { Area, Device, DraftStatus, Me, MnemoInfo, Perm, Tag } from './types';

// ------------------------------------------------------------------ авторизация
interface AuthState {
  me: Me | null;
  loaded: boolean;
  load: () => Promise<void>;
  login: (login: string, password: string) => Promise<void>;
  logout: () => Promise<void>;
  setMustChange: (v: boolean) => void;
}

export const useAuth = create<AuthState>((set, get) => ({
  me: null,
  loaded: false,
  load: async () => {
    try {
      set({ me: await api.get<Me>('/api/auth/me'), loaded: true });
    } catch {
      set({ me: null, loaded: true });
    }
  },
  login: async (login, password) => {
    const me = await api.post<Me>('/api/auth/login', { login, password });
    set({ me, loaded: true });
  },
  logout: async () => {
    try {
      await api.post('/api/auth/logout');
    } finally {
      set({ me: null });
    }
  },
  setMustChange: (v) => {
    const me = get().me;
    if (me) set({ me: { ...me, mustChangePassword: v } });
  },
}));

/** Есть ли право (в указанной области или хотя бы в одной). */
export function hasPerm(me: Me | null | undefined, perm: Perm, areaId?: number | null): boolean {
  const p = me?.permissions?.[perm];
  if (!p) return false;
  if (p === 'all') return true;
  if (areaId === undefined || areaId === null) return p.length > 0;
  return p.includes(areaId);
}

/** Хук: право в области (или хотя бы в одной; несколько кодов — любое из них). */
export function useCan(perm: Perm | Perm[], areaId?: number | null): boolean {
  const me = useAuth((s) => s.me);
  return (Array.isArray(perm) ? perm : [perm]).some((p) => hasPerm(me, p, areaId));
}

/** Функция проверки для списков (без хука на каждую строку). */
export function usePerms(): (perm: Perm, areaId?: number | null) => boolean {
  const me = useAuth((s) => s.me);
  return (perm, areaId) => hasPerm(me, perm, areaId);
}

/** Право на команду по тегу: класс и тип задают нужное право. */
export function commandPerm(tag: Pick<Tag, 'commandClass' | 'dataType'>): Perm {
  return tag.commandClass === 'critical' ? 'cmd.critical' : tag.dataType === 'bool' ? 'cmd.control' : 'cmd.setpoint';
}

// ------------------------------------------------------------------ области
interface AreasState {
  areas: Area[];
  byId: Map<number, Area>;
  reload: () => Promise<void>;
}

export const useAreas = create<AreasState>((set) => ({
  areas: [],
  byId: new Map(),
  reload: async () => {
    const areas = await api.get<Area[]>('/api/areas');
    set({ areas, byId: new Map(areas.map((a) => [a.id, a])) });
  },
}));

export const areaPath = (id: number | null | undefined) => (id ? useAreas.getState().byId.get(id)?.path ?? `#${id}` : '—');

// ------------------------------------------------------------------ метаданные (теги, устройства, схемы)
interface MetaState {
  tags: Tag[];
  tagById: Map<number, Tag>;
  tagByName: Map<string, Tag>;
  devices: Device[];
  mnemos: MnemoInfo[];
  loaded: boolean;
  reloadTags: () => Promise<void>;
  reloadDevices: () => Promise<void>;
  reloadMnemos: () => Promise<void>;
  reloadAll: () => Promise<void>;
}

export const useMeta = create<MetaState>((set, get) => ({
  tags: [],
  tagById: new Map(),
  tagByName: new Map(),
  devices: [],
  mnemos: [],
  loaded: false,
  reloadTags: async () => {
    const tags = await api.get<Tag[]>('/api/tags');
    set({
      tags,
      tagById: new Map(tags.map((t) => [t.id, t])),
      tagByName: new Map(tags.map((t) => [t.name, t])),
    });
  },
  reloadDevices: async () => set({ devices: await api.get<Device[]>('/api/devices') }),
  reloadMnemos: async () => set({ mnemos: await api.get<MnemoInfo[]>('/api/mnemos') }),
  reloadAll: async () => {
    const g = get();
    await Promise.all([g.reloadTags(), g.reloadDevices(), g.reloadMnemos()]);
    set({ loaded: true });
  },
}));

// изменения конфигурации в БД (любым инженером) → перечитать метаданные
let configTimer: ReturnType<typeof setTimeout> | null = null;
onRealtime('config', () => {
  if (configTimer) clearTimeout(configTimer);
  configTimer = setTimeout(() => {
    const m = useMeta.getState();
    void m.reloadTags().catch(() => {});
    void m.reloadDevices().catch(() => {});
  }, 800);
});
onRealtime('device', () => void useMeta.getState().reloadDevices().catch(() => {}));

// ------------------------------------------------------------------ черновик конфигурации
interface DraftState {
  status: DraftStatus | null;
  reload: () => Promise<void>;
}

export const useDraft = create<DraftState>((set) => ({
  status: null,
  reload: async () => {
    try { set({ status: await api.get<DraftStatus>('/api/draft/status') }); } catch { /* нет доступа */ }
  },
}));

let draftTimer: ReturnType<typeof setTimeout> | null = null;
const reloadDraft = () => {
  if (draftTimer) clearTimeout(draftTimer);
  draftTimer = setTimeout(() => void useDraft.getState().reload(), 400);
};
onRealtime('config', reloadDraft);
onRealtime('configApplied', reloadDraft);
// опубликована новая версия: операторам — новое дерево схем, инженерам — пустой черновик
onRealtime('published', () => {
  reloadDraft();
  void useMeta.getState().reloadMnemos().catch(() => {});
  void useMeta.getState().reloadTags().catch(() => {});
});

// ------------------------------------------------------------------ уведомления
export interface Toast {
  id: number;
  kind: 'info' | 'ok' | 'error';
  text: string;
}

interface ToastState {
  toasts: Toast[];
  push: (kind: Toast['kind'], text: string) => void;
  remove: (id: number) => void;
}

let toastId = 1;
export const useToasts = create<ToastState>((set, get) => ({
  toasts: [],
  push: (kind, text) => {
    const id = toastId++;
    set({ toasts: [...get().toasts, { id, kind, text }] });
    setTimeout(() => get().remove(id), kind === 'error' ? 8000 : 4000);
  },
  remove: (id) => set({ toasts: get().toasts.filter((t) => t.id !== id) }),
}));

export const toast = {
  ok: (t: string) => useToasts.getState().push('ok', t),
  info: (t: string) => useToasts.getState().push('info', t),
  error: (t: string) => useToasts.getState().push('error', t),
};
