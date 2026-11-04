import type { Tag } from './types';

// Коды качества совпадают с Scada.Core.Model.Quality
export const Q = {
  Good: 0,
  Uncertain: 64,
  UncertainEngineeringUnitsExceeded: 67,
  Bad: 128,
  BadConfigError: 129,
  BadNotConnected: 130,
  BadDeviceFailure: 131,
  BadCommFailure: 132,
  BadOutOfService: 133,
  BadWaitingForInitialData: 134,
  BadStale: 135,
  BadTypeMismatch: 136,
} as const;

export type QualityCategory = 'good' | 'uncertain' | 'bad';

export function qualityCategory(q: number | undefined): QualityCategory {
  if (q === undefined) return 'bad';
  if (q < 64) return 'good';
  if (q < 128) return 'uncertain';
  return 'bad';
}

const qualityNames: Record<number, string> = {
  0: 'Норма',
  1: 'Ручной ввод',
  64: 'Недостоверно',
  65: 'Последнее годное',
  66: 'Неточный датчик',
  67: 'Вне диапазона',
  128: 'Ошибка',
  129: 'Ошибка конфигурации',
  130: 'Нет связи',
  131: 'Отказ устройства',
  132: 'Обрыв связи',
  133: 'Выведен из работы',
  134: 'Ожидание данных',
  135: 'Устарело',
  136: 'Несовпадение типа',
};

export function qualityName(q: number | undefined): string {
  if (q === undefined) return 'Нет данных';
  return qualityNames[q] ?? `Код ${q}`;
}

export function formatNumber(v: number | null | undefined, decimals?: number): string {
  if (v === null || v === undefined || Number.isNaN(v)) return '—';
  const d = decimals ?? (Math.abs(v) >= 1000 ? 0 : Math.abs(v) >= 100 ? 1 : 2);
  return v.toLocaleString('ru-RU', { minimumFractionDigits: d, maximumFractionDigits: d });
}

export function formatValue(tag: Tag | undefined, num: number | null | undefined, str: string | null | undefined,
  decimals?: number): string {
  if (tag?.dataType === 'string') return str ?? '—';
  if (tag?.dataType === 'bool') return num === null || num === undefined ? '—' : num ? 'ВКЛ' : 'ОТКЛ';
  return formatNumber(num, decimals);
}

export function formatTime(iso: string | number | null | undefined, withDate = true): string {
  if (iso === null || iso === undefined) return '';
  const d = typeof iso === 'number' ? new Date(iso) : new Date(iso);
  return withDate
    ? d.toLocaleString('ru-RU', { day: '2-digit', month: '2-digit', year: '2-digit', hour: '2-digit', minute: '2-digit', second: '2-digit' })
    : d.toLocaleTimeString('ru-RU');
}

export const priorityNames: Record<number, string> = { 1: 'Авария', 2: 'Высокий', 3: 'Средний', 4: 'Низкий' };

export const alarmKindNames: Record<string, string> = {
  hihi: 'Верхняя аварийная (HH)',
  hi: 'Верхняя предупредительная (H)',
  lo: 'Нижняя предупредительная (L)',
  lolo: 'Нижняя аварийная (LL)',
  discrete_on: 'Дискретная: включено',
  discrete_off: 'Дискретная: отключено',
  bad_quality: 'Недостоверность / нет связи',
};

export const roleNames: Record<string, string> = {
  viewer: 'Наблюдатель',
  operator: 'Оператор',
  engineer: 'Инженер',
  admin: 'Администратор',
};

export const commandStatusNames: Record<string, string> = {
  pending: 'Ожидает',
  executing: 'Исполняется',
  done: 'Выполнена',
  failed: 'Ошибка',
  expired: 'Истекла',
  rejected: 'Отклонена',
};

/** ISO-строка для input[type=datetime-local] в локальном времени */
export function toLocalInput(d: Date): string {
  const p = (n: number) => String(n).padStart(2, '0');
  return `${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())}T${p(d.getHours())}:${p(d.getMinutes())}`;
}
