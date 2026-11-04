// Модель мнемосхемы. Схема хранится как JSON (mnemo_versions.doc), привязки — по ИМЕНИ тега,
// чтобы схему можно было экспортировать/импортировать между объектами.

export type ElementType =
  | 'rect' | 'ellipse' | 'line' | 'text' | 'value' | 'lamp' | 'button' | 'setpoint'
  | 'tank' | 'bar' | 'pump' | 'valve' | 'motor' | 'link' | 'trend';

export type PropValue = string | number | boolean | undefined;
export type Props = Record<string, PropValue>;

export interface MnemoElement {
  id: string;
  type: ElementType;
  x: number;
  y: number;
  /** Для линии w/h — смещение второго конца (могут быть отрицательными). */
  w: number;
  h: number;
  rotation?: number;
  locked?: boolean;
  props: Props;
}

export interface MnemoDoc {
  format: 'scada-mnemo';
  version: 1;
  width: number;
  height: number;
  background: string;
  grid: number;
  elements: MnemoElement[];
}

export type FieldType = 'text' | 'number' | 'color' | 'tag' | 'bool' | 'select' | 'mnemo';

export interface FieldDef {
  key: string;
  label: string;
  type: FieldType;
  options?: { value: string; label: string }[];
  /** для type=tag: только записываемые теги */
  writable?: boolean;
  hint?: string;
}

export interface ElementDef {
  type: ElementType;
  label: string;
  group: 'Графика' | 'Данные' | 'Управление' | 'Оборудование' | 'Навигация';
  size: [number, number];
  defaults: Props;
  fields: FieldDef[];
  isLine?: boolean;
}

const color = (key: string, label: string): FieldDef => ({ key, label, type: 'color' });
const num = (key: string, label: string, hint?: string): FieldDef => ({ key, label, type: 'number', hint });
const tag = (key: string, label: string, writable = false): FieldDef => ({ key, label, type: 'tag', writable });

export const COMMON_FIELDS: FieldDef[] = [
  tag('visTag', 'Видимость по тегу'),
  { key: 'visInvert', label: 'Видим при 0', type: 'bool' },
  { key: 'tooltip', label: 'Подсказка', type: 'text' },
];

export const ELEMENTS: Record<ElementType, ElementDef> = {
  rect: {
    type: 'rect', label: 'Прямоугольник', group: 'Графика', size: [120, 80],
    defaults: { fill: '#cbd5e1', stroke: '#475569', strokeWidth: 1, radius: 0, onFill: '' },
    fields: [color('fill', 'Заливка'), color('stroke', 'Контур'), num('strokeWidth', 'Толщина'), num('radius', 'Скругление'),
      tag('tag', 'Тег состояния'), color('onFill', 'Заливка при 1')],
  },
  ellipse: {
    type: 'ellipse', label: 'Эллипс', group: 'Графика', size: [80, 80],
    defaults: { fill: '#cbd5e1', stroke: '#475569', strokeWidth: 1, onFill: '' },
    fields: [color('fill', 'Заливка'), color('stroke', 'Контур'), num('strokeWidth', 'Толщина'),
      tag('tag', 'Тег состояния'), color('onFill', 'Заливка при 1')],
  },
  line: {
    type: 'line', label: 'Трубопровод / линия', group: 'Графика', size: [160, 0], isLine: true,
    defaults: { stroke: '#64748b', strokeWidth: 6, flowColor: '#2563eb', arrow: false },
    fields: [color('stroke', 'Цвет'), num('strokeWidth', 'Толщина'), tag('tag', 'Тег потока (1 — есть)'),
      color('flowColor', 'Цвет при потоке'), { key: 'arrow', label: 'Стрелка', type: 'bool' }],
  },
  text: {
    type: 'text', label: 'Надпись', group: 'Графика', size: [140, 28],
    defaults: { text: 'Надпись', fontSize: 14, color: '#1e293b', bold: false, align: 'start' },
    fields: [{ key: 'text', label: 'Текст', type: 'text' }, num('fontSize', 'Размер шрифта'), color('color', 'Цвет'),
      { key: 'bold', label: 'Жирный', type: 'bool' },
      { key: 'align', label: 'Выравнивание', type: 'select',
        options: [{ value: 'start', label: 'Влево' }, { value: 'middle', label: 'По центру' }, { value: 'end', label: 'Вправо' }] }],
  },
  value: {
    type: 'value', label: 'Значение', group: 'Данные', size: [110, 30],
    defaults: { tag: '', decimals: '', showUnit: true, fontSize: 15, label: '' },
    fields: [tag('tag', 'Тег'), num('decimals', 'Знаков после запятой', 'пусто — авто'), { key: 'showUnit', label: 'Ед. изм.', type: 'bool' },
      num('fontSize', 'Размер шрифта'), { key: 'label', label: 'Подпись слева', type: 'text' }],
  },
  lamp: {
    type: 'lamp', label: 'Лампа', group: 'Данные', size: [28, 28],
    defaults: { tag: '', onColor: '#16a34a', offColor: '#94a3b8', invert: false, label: '' },
    fields: [tag('tag', 'Тег'), color('onColor', 'Цвет «1»'), color('offColor', 'Цвет «0»'),
      { key: 'invert', label: 'Инверсия', type: 'bool' }, { key: 'label', label: 'Подпись', type: 'text' }],
  },
  button: {
    type: 'button', label: 'Кнопка', group: 'Управление', size: [110, 34],
    defaults: { label: 'Пуск', tag: '', mode: 'set', value: 1, fill: '#e2e8f0' },
    fields: [{ key: 'label', label: 'Надпись', type: 'text' }, tag('tag', 'Тег команды', true),
      { key: 'mode', label: 'Действие', type: 'select',
        options: [{ value: 'set', label: 'Записать значение' }, { value: 'toggle', label: 'Инвертировать (0↔1)' }] },
      num('value', 'Значение'), color('fill', 'Цвет')],
  },
  setpoint: {
    type: 'setpoint', label: 'Уставка', group: 'Управление', size: [120, 30],
    defaults: { tag: '', decimals: '', fontSize: 15 },
    fields: [tag('tag', 'Тег уставки', true), num('decimals', 'Знаков после запятой'), num('fontSize', 'Размер шрифта')],
  },
  tank: {
    type: 'tank', label: 'Ёмкость (уровень)', group: 'Данные', size: [90, 140],
    defaults: { tag: '', min: 0, max: 100, fill: '#60a5fa', showValue: true, decimals: 1 },
    fields: [tag('tag', 'Тег уровня'), num('min', 'Минимум'), num('max', 'Максимум'), color('fill', 'Цвет'),
      { key: 'showValue', label: 'Показывать значение', type: 'bool' }, num('decimals', 'Знаков после запятой')],
  },
  bar: {
    type: 'bar', label: 'Шкала', group: 'Данные', size: [140, 22],
    defaults: { tag: '', min: 0, max: 100, fill: '#64748b', vertical: false },
    fields: [tag('tag', 'Тег'), num('min', 'Минимум'), num('max', 'Максимум'), color('fill', 'Цвет'),
      { key: 'vertical', label: 'Вертикальная', type: 'bool' }],
  },
  pump: {
    type: 'pump', label: 'Насос', group: 'Оборудование', size: [64, 64],
    defaults: { stateTag: '', faultTag: '', cmdTag: '', label: '', onColor: '#16a34a', offColor: '#cbd5e1', faultColor: '#dc2626' },
    fields: [tag('stateTag', 'Тег «работа»'), tag('faultTag', 'Тег «авария»'), tag('cmdTag', 'Тег команды пуск/стоп', true),
      { key: 'label', label: 'Позиция', type: 'text' }, color('onColor', 'Цвет «работа»'), color('offColor', 'Цвет «стоп»'),
      color('faultColor', 'Цвет «авария»')],
  },
  motor: {
    type: 'motor', label: 'Двигатель', group: 'Оборудование', size: [60, 60],
    defaults: { stateTag: '', faultTag: '', cmdTag: '', label: '', onColor: '#16a34a', offColor: '#cbd5e1', faultColor: '#dc2626' },
    fields: [tag('stateTag', 'Тег «работа»'), tag('faultTag', 'Тег «авария»'), tag('cmdTag', 'Тег команды пуск/стоп', true),
      { key: 'label', label: 'Позиция', type: 'text' }, color('onColor', 'Цвет «работа»'), color('offColor', 'Цвет «стоп»'),
      color('faultColor', 'Цвет «авария»')],
  },
  valve: {
    type: 'valve', label: 'Задвижка / клапан', group: 'Оборудование', size: [60, 36],
    defaults: { stateTag: '', faultTag: '', cmdTag: '', label: '', onColor: '#16a34a', offColor: '#cbd5e1', faultColor: '#dc2626' },
    fields: [tag('stateTag', 'Тег «открыт»'), tag('faultTag', 'Тег «авария»'), tag('cmdTag', 'Тег команды откр./закр.', true),
      { key: 'label', label: 'Позиция', type: 'text' }, color('onColor', 'Цвет «открыт»'), color('offColor', 'Цвет «закрыт»'),
      color('faultColor', 'Цвет «авария»')],
  },
  link: {
    type: 'link', label: 'Переход на схему', group: 'Навигация', size: [130, 32],
    defaults: { label: 'Перейти →', target: '' },
    fields: [{ key: 'label', label: 'Надпись', type: 'text' }, { key: 'target', label: 'Схема', type: 'mnemo' }],
  },
  trend: {
    type: 'trend', label: 'Мини-тренд', group: 'Данные', size: [240, 110],
    defaults: { tag: '', minutes: 30, color: '#2563eb', min: '', max: '' },
    fields: [tag('tag', 'Тег'), num('minutes', 'Окно, мин'), color('color', 'Цвет'), num('min', 'Мин. шкалы', 'пусто — авто'),
      num('max', 'Макс. шкалы', 'пусто — авто')],
  },
};

export const TAG_KEYS = ['tag', 'stateTag', 'faultTag', 'cmdTag', 'visTag'] as const;

export function emptyDoc(): MnemoDoc {
  return { format: 'scada-mnemo', version: 1, width: 1600, height: 900, background: '#e2e8f0', grid: 10, elements: [] };
}

export function normalizeDoc(raw: unknown): MnemoDoc {
  const d = (raw && typeof raw === 'object' ? raw : {}) as Partial<MnemoDoc>;
  const base = emptyDoc();
  return {
    ...base,
    ...d,
    format: 'scada-mnemo',
    version: 1,
    elements: Array.isArray(d.elements)
      ? d.elements.filter((e) => e && ELEMENTS[e.type as ElementType]).map((e) => ({
          ...e,
          props: { ...ELEMENTS[e.type].defaults, ...(e.props ?? {}) },
        }))
      : [],
  };
}

export function newId(): string {
  return Math.random().toString(36).slice(2, 10);
}

export function createElement(type: ElementType, x: number, y: number): MnemoElement {
  const def = ELEMENTS[type];
  return { id: newId(), type, x, y, w: def.size[0], h: def.size[1], props: { ...def.defaults } };
}

/** Имена всех тегов, на которые ссылается схема. */
export function boundTagNames(doc: MnemoDoc): string[] {
  const set = new Set<string>();
  for (const el of doc.elements)
    for (const k of TAG_KEYS) {
      const v = el.props[k];
      if (typeof v === 'string' && v) set.add(v);
    }
  return [...set];
}

/** Габариты элемента (для линии — нормализованный прямоугольник). */
export function bounds(el: MnemoElement) {
  if (ELEMENTS[el.type].isLine) {
    const x1 = Math.min(el.x, el.x + el.w), y1 = Math.min(el.y, el.y + el.h);
    return { x: x1, y: y1, w: Math.abs(el.w), h: Math.abs(el.h) };
  }
  return { x: el.x, y: el.y, w: el.w, h: el.h };
}

export const str = (v: PropValue, d = ''): string => (v === undefined || v === null ? d : String(v));
export const numOr = (v: PropValue, d: number): number => {
  if (v === '' || v === undefined || v === null) return d;
  const n = Number(v);
  return Number.isFinite(n) ? n : d;
};
export const numOpt = (v: PropValue): number | undefined => {
  if (v === '' || v === undefined || v === null) return undefined;
  const n = Number(v);
  return Number.isFinite(n) ? n : undefined;
};
