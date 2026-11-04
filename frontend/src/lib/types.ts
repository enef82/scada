// DTO веб-API (camelCase, как отдаёт ASP.NET Core)

export type Role = 'viewer' | 'operator' | 'engineer' | 'admin';

export interface Me {
  login: string;
  displayName: string;
  role: Role;
  mustChangePassword: boolean;
}

export type Protocol = 'simulator' | 'modbus_tcp' | 'opcua';
export type DataType = 'bool' | 'int16' | 'uint16' | 'int32' | 'uint32' | 'float32' | 'float64' | 'string';
export type ArchiveMode = 'none' | 'change' | 'interval';

export interface Device {
  id: number;
  name: string;
  protocol: Protocol;
  endpoint: string;
  settings: Record<string, unknown>;
  enabled: boolean;
  pollMs: number;
  timeoutMs: number;
  description: string;
  state?: string | null;
  lastOkAt?: string | null;
  lastError?: string | null;
  errorCount?: number | null;
  avgLatencyMs?: number | null;
  rowVersion?: number;
}

export interface Tag {
  id: number;
  name: string;
  description: string;
  deviceId: number;
  address: string;
  dataType: DataType;
  unit: string;
  rawLow: number | null;
  rawHigh: number | null;
  engLow: number | null;
  engHigh: number | null;
  pollMs: number | null;
  staleAfterMs: number | null;
  deadbandUi: number;
  deadbandArchive: number;
  archiveMode: ArchiveMode;
  archiveIntervalS: number;
  writable: boolean;
  writeMin: number | null;
  writeMax: number | null;
  writeConfirm: boolean;
  enabled: boolean;
  rowVersion?: number;
}

export type AlarmKind = 'hihi' | 'hi' | 'lo' | 'lolo' | 'discrete_on' | 'discrete_off' | 'bad_quality';

export interface AlarmDef {
  id: number;
  tagId: number;
  kind: AlarmKind;
  limitValue: number | null;
  hysteresis: number;
  delayMs: number;
  priority: number;
  message: string;
  enabled: boolean;
  rowVersion?: number;
}

export interface ActiveAlarm {
  id: number;
  tagId: number;
  tagName: string;
  tagDescription: string;
  unit: string;
  kind: AlarmKind;
  priority: number;
  message: string;
  active: boolean;
  acked: boolean;
  value: number | null;
  raisedAt: string | null;
  clearedAt: string | null;
  ackedAt: string | null;
  ackedBy: string | null;
  shelvedUntil: string | null;
  comment: string | null;
}

export interface AlarmEvent {
  id: number;
  ts: string;
  alarmId: number;
  event: 'raise' | 'clear' | 'ack' | 'shelve' | 'unshelve' | 'comment';
  value: number | null;
  user: string | null;
  comment: string | null;
  tagName: string;
  message: string;
  priority: number;
}

export interface CommandRow {
  id: number;
  tagId: number;
  tagName: string;
  valueNum: number | null;
  valueStr: string | null;
  requestedBy: string;
  requestedAt: string;
  expiresAt: string;
  status: 'pending' | 'executing' | 'done' | 'failed' | 'expired' | 'rejected';
  result: string | null;
  readbackNum: number | null;
  finishedAt: string | null;
}

export interface MnemoInfo {
  id: number;
  name: string;
  parentId: number | null;
  sortOrder: number;
  currentVersion: number;
  updatedAt: string;
  updatedBy: string | null;
  /** опубликованная версия схемы (null — схема ещё не публиковалась) */
  publishedVersion?: number | null;
}

export interface UserRow {
  id: number;
  login: string;
  displayName: string;
  role: Role;
  enabled: boolean;
  mustChangePassword: boolean;
  createdAt: string;
  lastLoginAt: string | null;
}

export interface AuditRow {
  id: number;
  ts: string;
  user: string | null;
  action: string;
  object: string;
  details: unknown;
  ip: string | null;
}

/** Значение тега в реальном времени: [id, num, str, quality, tsMs] */
export type TagValueTuple = [number, number | null, string | null, number, number];

// ------------------------------------------------------------------ публикация конфигурации
export type ObjectType = 'device' | 'tag' | 'alarm' | 'mnemo';

export interface DraftStatus {
  publishedVersion: number;
  publishedAt: string;
  publishedBy: string;
  appliedAt: string | null;
  draftChanges: number;
  draftAuthors: string[];
}

export interface FieldChange { field: string; old: unknown; new: unknown }

export interface ChangeItem {
  objectType: ObjectType;
  id: number;
  name: string;
  op: 'created' | 'updated' | 'deleted';
  fields: FieldChange[];
  highlight: boolean;
}

export interface ValidationIssue {
  code: string;
  level: 'error' | 'warning';
  message: string;
  objectType: ObjectType | 'config';
  objectId: number | null;
}

export type ChangeSummary = Partial<Record<ObjectType, Partial<Record<ChangeItem['op'], number>>>>;

export interface DraftDiff {
  baseVersion: number;
  summary: ChangeSummary;
  changes: { change: ChangeItem; authors: string[]; lastAt: string | null }[];
  errors: ValidationIssue[];
  warnings: ValidationIssue[];
}

export interface ConfigVersion {
  version: number;
  createdAt: string;
  createdBy: string;
  comment: string;
  rollbackOf: number | null;
  appliedAt: string | null;
  appliedMs: number | null;
  summary: ChangeSummary;
  warnings: number;
}
