// Исходные данные демо-режима: те же устройства, теги, тревоги и схемы, что в db/002_demo.sql,
// плюс тег режима «Автомат» и обзорная схема.
import type { AlarmDef, Device, Role, Tag, UserRow } from '../lib/types';
import type { MnemoDoc, MnemoElement } from '../mnemo/model';
import seedMnemos from './mnemos.json';

export const devicesSeed: Device[] = [
  {
    id: 1, name: 'ПЛК КНС-1', protocol: 'modbus_tcp', endpoint: '192.168.10.21:502',
    settings: { unitId: 1, byteOrder: 'ABCD' }, enabled: true, pollMs: 500, timeoutMs: 1000,
    description: 'Шкаф управления насосами, щит ЩУ-1',
  },
  {
    id: 2, name: 'ПЛК КНС-2', protocol: 'modbus_tcp', endpoint: '192.168.10.22:502',
    settings: { unitId: 1, byteOrder: 'ABCD' }, enabled: true, pollMs: 1000, timeoutMs: 1500,
    description: 'Связь через GSM-модем',
  },
];

type TagSeed = Partial<Tag> & Pick<Tag, 'name' | 'description' | 'deviceId' | 'address' | 'dataType'>;

const base: Omit<Tag, 'id' | 'name' | 'description' | 'deviceId' | 'address' | 'dataType'> = {
  unit: '', rawLow: null, rawHigh: null, engLow: null, engHigh: null, pollMs: null, staleAfterMs: null,
  deadbandUi: 0, deadbandArchive: 0, archiveMode: 'change', archiveIntervalS: 60,
  writable: false, writeMin: null, writeMax: null, writeConfirm: true, enabled: true,
};

const tagList: TagSeed[] = [
  { name: 'KNS1_Level', description: 'Уровень в приёмном резервуаре', deviceId: 1, address: 'ir:0', dataType: 'uint16', unit: 'м', rawLow: 0, rawHigh: 10000, engLow: 0, engHigh: 4, deadbandUi: 0.005, deadbandArchive: 0.01 },
  { name: 'KNS1_Level_SP', description: 'Уставка уровня пуска', deviceId: 1, address: 'hr:10', dataType: 'float32', unit: 'м', writable: true, writeMin: 1.5, writeMax: 3.4 },
  { name: 'KNS1_P1_Run', description: 'Н-1 в работе', deviceId: 1, address: 'di:0', dataType: 'bool' },
  { name: 'KNS1_P1_Cmd', description: 'Н-1 команда пуск/стоп', deviceId: 1, address: 'co:0', dataType: 'bool', writable: true, archiveMode: 'none' },
  { name: 'KNS1_P1_Fault', description: 'Н-1 авария', deviceId: 1, address: 'di:1', dataType: 'bool' },
  { name: 'KNS1_P1_Current', description: 'Н-1 ток двигателя', deviceId: 1, address: 'ir:2', dataType: 'uint16', unit: 'А', rawLow: 0, rawHigh: 1000, engLow: 0, engHigh: 100, deadbandUi: 0.2, deadbandArchive: 0.5 },
  { name: 'KNS1_P1_Temp', description: 'Н-1 температура обмотки', deviceId: 1, address: 'ir:3', dataType: 'int16', unit: '°C', rawLow: 0, rawHigh: 1000, engLow: 0, engHigh: 100, deadbandUi: 0.1, deadbandArchive: 0.5 },
  { name: 'KNS1_P2_Run', description: 'Н-2 в работе', deviceId: 1, address: 'di:2', dataType: 'bool' },
  { name: 'KNS1_P2_Cmd', description: 'Н-2 команда пуск/стоп', deviceId: 1, address: 'co:1', dataType: 'bool', writable: true, archiveMode: 'none' },
  { name: 'KNS1_P2_Current', description: 'Н-2 ток двигателя', deviceId: 1, address: 'ir:4', dataType: 'uint16', unit: 'А', rawLow: 0, rawHigh: 1000, engLow: 0, engHigh: 100, deadbandUi: 0.2, deadbandArchive: 0.5 },
  { name: 'KNS1_Pressure', description: 'Давление в напорном коллекторе', deviceId: 1, address: 'ir:6:cdab', dataType: 'float32', unit: 'бар', deadbandUi: 0.01, deadbandArchive: 0.02 },
  { name: 'KNS1_Flow', description: 'Расход', deviceId: 1, address: 'ir:8:cdab', dataType: 'float32', unit: 'м³/ч', deadbandUi: 1, deadbandArchive: 2, archiveMode: 'interval', archiveIntervalS: 10 },
  { name: 'KNS1_Valve1_Open', description: 'ЗД-1 открыта', deviceId: 1, address: 'di:3', dataType: 'bool' },
  { name: 'KNS1_Valve1_Cmd', description: 'ЗД-1 команда откр./закр.', deviceId: 1, address: 'co:2', dataType: 'bool', writable: true, archiveMode: 'none' },
  { name: 'KNS1_Door', description: 'Дверь КНС открыта', deviceId: 1, address: 'di:4', dataType: 'bool' },
  { name: 'KNS2_Level', description: 'КНС-2 уровень', deviceId: 2, address: 'hr:0', dataType: 'float32', unit: 'м', deadbandUi: 0.01, deadbandArchive: 0.01 },
  { name: 'KNS2_P1_Run', description: 'КНС-2 Н-1 в работе', deviceId: 2, address: 'co:0', dataType: 'bool' },
  { name: 'KNS1_Auto', description: 'КНС-1 режим «Автомат»', deviceId: 1, address: 'co:5', dataType: 'bool', writable: true },
];

export const tagsSeed: Tag[] = tagList.map((t, i) => ({ ...base, ...t, id: i + 1 }) as Tag);

const tid = (name: string) => tagsSeed.find((t) => t.name === name)!.id;

export const alarmDefsSeed: AlarmDef[] = [
  { tag: 'KNS1_Level', kind: 'hihi', limitValue: 3.5, hysteresis: 0.05, delayMs: 0, priority: 1, message: 'Аварийно высокий уровень в резервуаре' },
  { tag: 'KNS1_Level', kind: 'hi', limitValue: 3.0, hysteresis: 0.05, delayMs: 2000, priority: 2, message: 'Высокий уровень в резервуаре' },
  { tag: 'KNS1_Level', kind: 'lo', limitValue: 0.7, hysteresis: 0.05, delayMs: 2000, priority: 3, message: 'Низкий уровень — защита от сухого хода' },
  { tag: 'KNS1_Pressure', kind: 'hi', limitValue: 3.6, hysteresis: 0.1, delayMs: 3000, priority: 2, message: 'Повышенное давление в коллекторе' },
  { tag: 'KNS1_P1_Temp', kind: 'hi', limitValue: 70, hysteresis: 1, delayMs: 5000, priority: 2, message: 'Перегрев обмотки двигателя Н-1' },
  { tag: 'KNS1_P1_Fault', kind: 'discrete_on', limitValue: null, hysteresis: 0, delayMs: 0, priority: 1, message: 'Авария насоса Н-1' },
  { tag: 'KNS1_Door', kind: 'discrete_on', limitValue: null, hysteresis: 0, delayMs: 0, priority: 4, message: 'Открыта дверь КНС' },
  { tag: 'KNS1_Level', kind: 'bad_quality', limitValue: null, hysteresis: 0, delayMs: 3000, priority: 2, message: 'Нет связи с ПЛК КНС-1' },
  { tag: 'KNS2_Level', kind: 'bad_quality', limitValue: null, hysteresis: 0, delayMs: 5000, priority: 2, message: 'Нет связи с ПЛК КНС-2' },
].map((a, i) => ({ id: i + 1, tagId: tid(a.tag), kind: a.kind as AlarmDef['kind'], limitValue: a.limitValue, hysteresis: a.hysteresis,
  delayMs: a.delayMs, priority: a.priority, message: a.message, enabled: true }));

export interface DemoUser extends UserRow { password?: string }

const now = new Date().toISOString();
export const usersSeed: DemoUser[] = [
  { id: 1, login: 'ivanov', displayName: 'Иванов С. П.', role: 'operator', enabled: true, mustChangePassword: false, createdAt: now, lastLoginAt: now },
  { id: 2, login: 'petrova', displayName: 'Петрова А. В.', role: 'engineer', enabled: true, mustChangePassword: false, createdAt: now, lastLoginAt: null },
  { id: 3, login: 'admin', displayName: 'Администратор', role: 'admin', enabled: true, mustChangePassword: false, createdAt: now, lastLoginAt: null },
  { id: 4, login: 'dispatcher', displayName: 'Диспетчер района', role: 'viewer', enabled: true, mustChangePassword: false, createdAt: now, lastLoginAt: null },
];

export const roleUser: Record<Role, string> = { operator: 'ivanov', engineer: 'petrova', admin: 'admin', viewer: 'dispatcher' };

// ------------------------------------------------------------------ схемы

let n = 1000;
const el = (type: MnemoElement['type'], x: number, y: number, w: number, h: number, props: MnemoElement['props']): MnemoElement =>
  ({ id: `d${n++}`, type, x, y, w, h, props });

function kns1(): MnemoDoc {
  const doc = structuredClone((seedMnemos as Record<string, MnemoDoc>)['1']);
  doc.elements = doc.elements.filter((e) => e.type !== 'link');
  for (const e of doc.elements) {
    if (e.type === 'text' && String(e.props.text).startsWith('Демонстрационная')) e.props.text = 'Приёмный резервуар, два насоса, напорный коллектор';
  }
  doc.elements.push(
    el('rect', 900, 96, 380, 56, { fill: '#f1f5f9', stroke: '#94a3b8', strokeWidth: 1, radius: 4, onFill: '' }),
    el('text', 914, 110, 110, 28, { text: 'Режим КНС', fontSize: 13, color: '#475569', bold: true, align: 'start' }),
    el('lamp', 1012, 110, 26, 26, { tag: 'KNS1_Auto', onColor: '#2563eb', offColor: '#94a3b8', invert: false, label: '' }),
    el('value', 1048, 109, 90, 28, { tag: 'KNS1_Auto', decimals: '', showUnit: false, fontSize: 13, label: 'Авт.' }),
    el('button', 1148, 107, 120, 32, { label: 'Авто ⇄ Ручной', tag: 'KNS1_Auto', mode: 'toggle', value: 1, fill: '#e0e7ff' }),
    el('link', 1400, 30, 170, 34, { label: '← Обзор', target: 3 }),
    el('lamp', 1460, 110, 26, 26, { tag: 'KNS1_P2_Run', onColor: '#16a34a', offColor: '#94a3b8', invert: true, label: 'Н-2 стоп' }),
  );
  // последняя лампа — заготовка для задания №8, по умолчанию её нет
  doc.elements.pop();
  return doc;
}

function kns2(): MnemoDoc {
  const doc = structuredClone((seedMnemos as Record<string, MnemoDoc>)['2']);
  for (const e of doc.elements) {
    if (e.type === 'text' && String(e.props.text).startsWith('ПЛК по Modbus')) e.props.text = 'Связь через GSM-модем · 192.168.10.22';
    if (e.type === 'link') { e.props.label = '← Обзор'; e.props.target = 3; }
  }
  return doc;
}

function overview(): MnemoDoc {
  const card = (x: number, title: string, target: number, level: string, extra: MnemoElement[]) => [
    el('rect', x, 150, 600, 460, { fill: '#f8fafc', stroke: '#94a3b8', strokeWidth: 1, radius: 6, onFill: '' }),
    el('text', x + 24, 168, 400, 34, { text: title, fontSize: 22, bold: true, color: '#1e293b', align: 'start' }),
    el('link', x + 420, 168, 160, 34, { label: 'Открыть схему →', target }),
    el('tank', x + 24, 230, 120, 340, { tag: level, min: 0, max: 4, fill: '#7dd3fc', showValue: false, decimals: 2 }),
    el('value', x + 170, 230, 200, 30, { tag: level, decimals: 2, showUnit: true, fontSize: 15, label: 'Уровень' }),
    ...extra,
  ];
  const doc: MnemoDoc = {
    format: 'scada-mnemo', version: 1, width: 1600, height: 860, background: '#e2e8f0', grid: 10,
    elements: [
      el('text', 30, 20, 900, 36, { text: 'Канализационные насосные станции — обзор', fontSize: 24, bold: true, color: '#1e293b', align: 'start' }),
      el('text', 30, 58, 900, 24, { text: 'Щелчок по насосу открывает панель управления, по кнопке — переход на подробную схему', fontSize: 13, color: '#64748b', align: 'start' }),
      ...card(60, 'КНС-1', 1, 'KNS1_Level', [
        el('value', 370 - 200 + 0, 270, 200, 30, { tag: 'KNS1_Pressure', decimals: 2, showUnit: true, fontSize: 15, label: 'Давление' }),
        el('value', 170, 310, 200, 30, { tag: 'KNS1_Flow', decimals: 0, showUnit: true, fontSize: 15, label: 'Расход' }),
        el('pump', 200, 400, 56, 56, { stateTag: 'KNS1_P1_Run', faultTag: 'KNS1_P1_Fault', cmdTag: 'KNS1_P1_Cmd', label: 'Н-1', onColor: '#16a34a', offColor: '#cbd5e1', faultColor: '#dc2626' }),
        el('pump', 300, 400, 56, 56, { stateTag: 'KNS1_P2_Run', faultTag: '', cmdTag: 'KNS1_P2_Cmd', label: 'Н-2', onColor: '#16a34a', offColor: '#cbd5e1', faultColor: '#dc2626' }),
        el('lamp', 420, 412, 26, 26, { tag: 'KNS1_Auto', onColor: '#2563eb', offColor: '#94a3b8', invert: false, label: 'Автомат' }),
        el('lamp', 500, 412, 26, 26, { tag: 'KNS1_Door', onColor: '#f59e0b', offColor: '#94a3b8', invert: false, label: 'Дверь' }),
        el('trend', 170, 490, 410, 100, { tag: 'KNS1_Level', minutes: 15, color: '#0284c7', min: 0, max: 4 }),
      ]),
      ...card(740, 'КНС-2', 2, 'KNS2_Level', [
        el('pump', 880, 400, 56, 56, { stateTag: 'KNS2_P1_Run', faultTag: '', cmdTag: '', label: 'Н-1', onColor: '#16a34a', offColor: '#cbd5e1', faultColor: '#dc2626' }),
        el('trend', 910, 490, 410, 100, { tag: 'KNS2_Level', minutes: 15, color: '#0284c7', min: 0, max: 4 }),
      ]),
    ],
  };
  return doc;
}

export interface MnemoSeed { id: number; name: string; parentId: number | null; sortOrder: number; doc: MnemoDoc }

export function mnemosSeed(): MnemoSeed[] {
  return [
    { id: 3, name: 'Обзор', parentId: null, sortOrder: 0, doc: overview() },
    { id: 1, name: 'КНС-1', parentId: 3, sortOrder: 1, doc: kns1() },
    { id: 2, name: 'КНС-2', parentId: 3, sortOrder: 2, doc: kns2() },
  ];
}
