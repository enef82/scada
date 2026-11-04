import { useCallback, useEffect, useMemo, useState } from 'react';
import { NavLink } from 'react-router-dom';
import { AreaSelect, Badge, Empty, Field, Modal, areaTree, askConfirm } from '../components/ui';
import { api, errorText } from '../lib/api';
import { formatTime } from '../lib/format';
import { toast, useAreas, useAuth, useCan } from '../lib/stores';
import { PERMS, type Area, type AssignmentRow, type Perm, type RoleRow, type UserRow } from '../lib/types';

/** Вкладки раздела «Доступ». */
function AccessTabs() {
  const users = useCan('users.manage');
  return (
    <div className="tabs">
      {users && <NavLink to="/admin/users">Пользователи</NavLink>}
      {users && <NavLink to="/admin/assignments">Назначения и группы AD</NavLink>}
      {users && <NavLink to="/admin/roles">Роли</NavLink>}
      <NavLink to="/admin/areas">Области</NavLink>
    </div>
  );
}

const toDate = (iso: string | null) => (iso ? iso.slice(0, 10) : '');

// ------------------------------------------------------------------ пользователи
export function UsersPage() {
  const [rows, setRows] = useState<UserRow[]>([]);
  const [edit, setEdit] = useState<Partial<UserRow> | null>(null);
  const [assign, setAssign] = useState<string | null>(null);
  const me = useAuth((s) => s.me);
  const load = useCallback(async () => {
    try { setRows(await api.get<UserRow[]>('/api/users')); } catch (e) { toast.error(errorText(e)); }
  }, []);
  useEffect(() => { void load(); }, [load]);

  const remove = async (u: UserRow) => {
    if (!(await askConfirm(`Удалить пользователя ${u.login} и все его личные назначения? Действует сразу.`, { ok: 'Удалить', danger: true }))) return;
    try { await api.del(`/api/users/${u.id}`); void load(); } catch (e) { toast.error(errorText(e)); }
  };

  return (
    <div className="page">
      <AccessTabs />
      <div className="toolbar">
        <h3>Пользователи</h3>
        <span className="muted small">Права выдаются назначениями: роль + область. Изменения действуют сразу и пишутся в аудит.</span>
        <span className="grow" />
        <button className="primary" onClick={() => setEdit({ login: '', displayName: '', enabled: true, emergency: false, source: 'local' })}>Добавить локального</button>
      </div>
      <table className="table">
        <thead><tr><th>Логин</th><th>Имя</th><th>Тип</th><th>Роли и области</th><th>Последний вход</th><th /></tr></thead>
        <tbody>
          {rows.map((u) => (
            <tr key={u.id} className={u.enabled ? '' : 'row-disabled'}>
              <td>
                <b>{u.login}</b>{u.mustChangePassword && u.source === 'local' && <Badge kind="warn">смена пароля</Badge>}
                {!u.enabled && <Badge kind="muted">отключён</Badge>}
              </td>
              <td>{u.displayName}</td>
              <td>{u.source === 'ad' ? <Badge kind="info">домен</Badge> : u.emergency ? <Badge kind="bad">аварийная</Badge> : 'локальная'}</td>
              <td className="small">
                {u.roles.length ? u.roles.map((r) => <div key={r}>{r}</div>) : <span className="muted">{u.source === 'ad' ? 'только через группы AD' : 'нет назначений'}</span>}
              </td>
              <td>{formatTime(u.lastLoginAt)}</td>
              <td className="actions">
                <button onClick={() => setAssign(u.login)}>Назначения</button>
                <button onClick={() => setEdit(u)}>Изменить</button>
                <button disabled={u.login === me?.login} onClick={() => void remove(u)}>×</button>
              </td>
            </tr>
          ))}
        </tbody>
      </table>
      {edit && <UserForm u={edit} onClose={() => setEdit(null)} onSaved={(login) => { setEdit(null); void load(); if (login) setAssign(login); }} />}
      {assign && (
        <Modal title={`Назначения: ${assign}`} wide onClose={() => { setAssign(null); void load(); }}>
          <AssignmentsTable principalType="user" principal={assign} />
        </Modal>
      )}
    </div>
  );
}

function UserForm({ u, onClose, onSaved }: { u: Partial<UserRow>; onClose: () => void; onSaved: (newLogin?: string) => void }) {
  const [login, setLogin] = useState(u.login ?? '');
  const [displayName, setName] = useState(u.displayName ?? '');
  const [enabled, setEnabled] = useState(u.enabled ?? true);
  const [emergency, setEmergency] = useState(u.emergency ?? false);
  const [password, setPassword] = useState('');
  const ad = u.source === 'ad';
  const save = async () => {
    try {
      const body = { login, displayName, enabled, emergency, password: password || null };
      if (u.id) await api.put(`/api/users/${u.id}`, body); else await api.post('/api/users', body);
      toast.ok(u.id ? 'Сохранено' : 'Пользователь создан — выдайте ему роль');
      onSaved(u.id ? undefined : login.trim());
    } catch (e) { toast.error(errorText(e)); }
  };
  return (
    <Modal title={u.id ? `Пользователь ${u.login}` : 'Новый локальный пользователь'} onClose={onClose}
      footer={<><button onClick={onClose}>Отмена</button><button className="primary" onClick={save}>Сохранить</button></>}>
      <div className="form">
        <Field label="Логин"><input id="user-login" value={login} disabled={!!u.id} onChange={(e) => setLogin(e.target.value)} /></Field>
        <Field label="Имя (ФИО)"><input value={displayName} onChange={(e) => setName(e.target.value)} disabled={ad} /></Field>
        <Field label="Активен" hint="отключение действует сразу, сеансы завершаются в течение минуты">
          <input type="checkbox" checked={enabled} onChange={(e) => setEnabled(e.target.checked)} />
        </Field>
        {!ad && (
          <Field label="Аварийная запись" hint="для входа, когда домен недоступен; каждый вход — отдельная пометка в аудите и оповещение">
            <input type="checkbox" checked={emergency} onChange={(e) => setEmergency(e.target.checked)} />
          </Field>
        )}
        {!ad && (
          <Field label={u.id ? 'Новый пароль (сброс)' : 'Пароль'} hint="не короче 8 символов; при первом входе потребуется смена" wide>
            <input type="password" value={password} onChange={(e) => setPassword(e.target.value)} />
          </Field>
        )}
      </div>
      {ad && <p className="muted small">Доменная учётная запись: пароль и ФИО берутся из Active Directory. Отключить вход в SCADA можно флажком «Активен».</p>}
    </Modal>
  );
}

// ------------------------------------------------------------------ назначения
export function AssignmentsPage() {
  const [status, setStatus] = useState<{ enabled: boolean; lastOkAt: string | null; lastErrorAt: string | null; lastError: string | null } | null>(null);
  const [test, setTest] = useState('');
  const [testResult, setTestResult] = useState<string | null>(null);
  useEffect(() => { void api.get<typeof status>('/api/directory/status').then(setStatus).catch(() => {}); }, []);
  const runTest = async () => {
    try {
      const r = await api.post<{ status: string; user: { displayName: string; groups: string[]; disabled: boolean } | null; error: string | null }>('/api/directory/test', { login: test });
      setTestResult(r.user ? `${r.user.displayName}: ${r.user.groups.join(', ') || 'нет групп'}${r.user.disabled ? ' (отключена)' : ''}` : `${r.status}${r.error ? ': ' + r.error : ''}`);
    } catch (e) { setTestResult(errorText(e)); }
  };
  return (
    <div className="page">
      <AccessTabs />
      <div className="toolbar">
        <h3>Назначения</h3>
        <span className="muted small">Итоговые права пользователя = его личные назначения + назначения его групп AD. Запрещающих правил нет.</span>
      </div>
      <section className="card-block">
        <h4>Группы Active Directory</h4>
        {status && (
          <p className="small">
            {status.enabled
              ? <>Интеграция включена (LDAPS). Последний успешный обмен: {formatTime(status.lastOkAt)}{status.lastError && <span className="warn"> · ошибка {formatTime(status.lastErrorAt)}: {status.lastError}</span>}</>
              : <span className="muted">Интеграция с AD выключена (Directory:Enabled) — работают локальные учётные записи. Сопоставления групп начнут действовать после включения.</span>}
          </p>
        )}
        {status?.enabled && (
          <div className="row">
            <input placeholder="логин для проверки" value={test} onChange={(e) => setTest(e.target.value)} />
            <button disabled={!test.trim()} onClick={() => void runTest()}>Прочитать группы из AD</button>
            {testResult && <span className="small">{testResult}</span>}
          </div>
        )}
        <AssignmentsTable principalType="group" />
      </section>
      <section className="card-block">
        <h4>Личные назначения</h4>
        <AssignmentsTable principalType="user" />
      </section>
    </div>
  );
}

function AssignmentsTable({ principalType, principal }: { principalType: 'user' | 'group'; principal?: string }) {
  const [rows, setRows] = useState<AssignmentRow[]>([]);
  const [roles, setRoles] = useState<RoleRow[]>([]);
  const [edit, setEdit] = useState<Partial<AssignmentRow> | null>(null);
  const load = useCallback(async () => {
    try {
      const q = new URLSearchParams({ principalType, ...(principal ? { principal } : {}) });
      setRows(await api.get<AssignmentRow[]>(`/api/assignments?${q}`));
    } catch (e) { toast.error(errorText(e)); }
  }, [principalType, principal]);
  useEffect(() => { void load(); void api.get<RoleRow[]>('/api/roles').then(setRoles).catch(() => {}); }, [load]);

  const remove = async (a: AssignmentRow) => {
    if (!(await askConfirm(`Отозвать роль «${a.roleName}» у ${a.principal} на области «${a.areaPath}»? Действует сразу.`, { ok: 'Отозвать', danger: true }))) return;
    try { await api.del(`/api/assignments/${a.id}`); void load(); } catch (e) { toast.error(errorText(e)); }
  };

  return (
    <>
      {rows.length === 0 ? <Empty>{principalType === 'group' ? 'Сопоставлений групп нет' : 'Назначений нет'}</Empty> : (
        <table className="table">
          <thead><tr><th>{principalType === 'group' ? 'Группа AD' : 'Пользователь'}</th><th>Роль</th><th>Область</th><th>Срок</th><th>Комментарий</th><th /></tr></thead>
          <tbody>
            {rows.map((a) => (
              <tr key={a.id} className={a.expired ? 'row-disabled' : ''}>
                <td><b>{a.principal}</b></td>
                <td>{a.roleName}</td>
                <td className="small">{a.areaPath}</td>
                <td className="small">{a.validUntil ? <>{a.expired ? <Badge kind="muted">истекло</Badge> : 'до'} {new Date(a.validUntil).toLocaleDateString('ru-RU')}</> : 'бессрочно'}</td>
                <td className="small muted">{a.comment}</td>
                <td className="actions">
                  <button onClick={() => setEdit(a)}>Изменить</button>
                  <button onClick={() => void remove(a)}>×</button>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
      <div className="row">
        <button onClick={() => setEdit({ principalType, principal: principal ?? '', roleId: roles.find((r) => r.code === 'viewer')?.id, areaId: 1, validUntil: null, comment: '' })}>
          {principalType === 'group' ? '+ Сопоставить группу AD' : '+ Выдать роль'}
        </button>
      </div>
      {edit && <AssignmentForm a={edit} roles={roles} fixedPrincipal={!!principal} onClose={() => setEdit(null)} onSaved={() => { setEdit(null); void load(); }} />}
    </>
  );
}

function AssignmentForm({ a, roles, fixedPrincipal, onClose, onSaved }: {
  a: Partial<AssignmentRow>; roles: RoleRow[]; fixedPrincipal: boolean; onClose: () => void; onSaved: () => void;
}) {
  const [x, setX] = useState(a);
  const role = roles.find((r) => r.id === x.roleId);
  const rootOnly = role?.permissions.filter((p) => PERMS.find((d) => d.code === p)?.rootOnly) ?? [];
  const save = async () => {
    try {
      const body = { principalType: x.principalType, principal: x.principal, roleId: x.roleId, areaId: x.areaId,
        validUntil: x.validUntil ? new Date(`${toDate(x.validUntil)}T23:59:59`).toISOString() : null, comment: x.comment };
      if (x.id) await api.put(`/api/assignments/${x.id}`, body); else await api.post('/api/assignments', body);
      toast.ok('Назначение сохранено — действует сразу');
      onSaved();
    } catch (e) { toast.error(errorText(e)); }
  };
  const group = x.principalType === 'group';
  return (
    <Modal title={x.id ? 'Назначение' : group ? 'Сопоставление группы AD' : 'Новое назначение'} onClose={onClose}
      footer={<><button onClick={onClose}>Отмена</button><button className="primary" onClick={save}>Сохранить</button></>}>
      <div className="form">
        <Field label={group ? 'Группа AD (cn)' : 'Логин'} hint={group ? 'например SCADA-Operators-KNS; вложенные группы учитываются' : undefined}>
          <input id="asg-principal" value={x.principal ?? ''} disabled={fixedPrincipal} onChange={(e) => setX({ ...x, principal: e.target.value })} />
        </Field>
        <Field label="Роль">
          <select id="asg-role" value={x.roleId ?? ''} onChange={(e) => setX({ ...x, roleId: Number(e.target.value) })}>
            {roles.map((r) => <option key={r.id} value={r.id}>{r.name}{r.isSystem ? '' : ' (своя)'}</option>)}
          </select>
        </Field>
        <Field label="Область" hint="действует на неё и все вложенные" wide>
          <AreaSelect id="asg-area" value={x.areaId} onChange={(v) => setX({ ...x, areaId: v ?? 1 })} />
        </Field>
        <Field label="Действует до" hint="пусто — бессрочно; для подрядчиков и наладчиков">
          <input type="date" value={toDate(x.validUntil ?? null)} onChange={(e) => setX({ ...x, validUntil: e.target.value || null })} />
        </Field>
        <Field label="Комментарий" wide><input value={x.comment ?? ''} onChange={(e) => setX({ ...x, comment: e.target.value })} placeholder="приказ, заявка, договор" /></Field>
      </div>
      {role && <p className="small">Права роли: {role.permissions.map((p) => PERMS.find((d) => d.code === p)?.name ?? p).join(', ') || '—'}</p>}
      {rootOnly.length > 0 && x.areaId !== 1 && (
        <div className="alert alert-warn">Права «{rootOnly.map((p) => PERMS.find((d) => d.code === p)?.name).join('», «')}» действуют только на корневой области.</div>
      )}
    </Modal>
  );
}

// ------------------------------------------------------------------ роли
export function RolesPage() {
  const [roles, setRoles] = useState<RoleRow[]>([]);
  const [edit, setEdit] = useState<Partial<RoleRow> | null>(null);
  const load = useCallback(async () => {
    try { setRoles(await api.get<RoleRow[]>('/api/roles')); } catch (e) { toast.error(errorText(e)); }
  }, []);
  useEffect(() => { void load(); }, [load]);
  const remove = async (r: RoleRow) => {
    if (!(await askConfirm(`Удалить роль «${r.name}»?`, { ok: 'Удалить', danger: true }))) return;
    try { await api.del(`/api/roles/${r.id}`); void load(); } catch (e) { toast.error(errorText(e)); }
  };
  return (
    <div className="page">
      <AccessTabs />
      <div className="toolbar">
        <h3>Роли — наборы прав</h3>
        <span className="muted small">Системные роли не меняются: скопируйте и измените копию. Администратор безопасности намеренно не управляет оборудованием.</span>
        <span className="grow" />
        <button className="primary" onClick={() => setEdit({ name: '', description: '', permissions: ['view'] })}>Создать роль</button>
      </div>
      <div className="matrix-wrap">
        <table className="table matrix">
          <thead>
            <tr><th>Право</th>{roles.map((r) => <th key={r.id} title={r.description}>{r.name}{!r.isSystem && <div><Badge kind="info">своя</Badge></div>}</th>)}</tr>
          </thead>
          <tbody>
            {PERMS.map((p) => (
              <tr key={p.code}>
                <td title={p.hint}><b>{p.name}</b>{p.rootOnly && <span className="muted small"> · только корень</span>}<div className="muted small">{p.hint}</div></td>
                {roles.map((r) => <td key={r.id} className="matrix-cell">{r.permissions.includes(p.code) ? '✓' : ''}</td>)}
              </tr>
            ))}
            <tr>
              <td className="muted small">Назначений</td>
              {roles.map((r) => <td key={r.id} className="matrix-cell small">{r.assignments}</td>)}
            </tr>
            <tr>
              <td />
              {roles.map((r) => (
                <td key={r.id} className="matrix-cell">
                  <div className="actions">
                    <button onClick={() => setEdit({ name: `${r.name} (копия)`, description: r.description, permissions: [...r.permissions] })}>Копировать</button>
                    {!r.isSystem && <button onClick={() => setEdit(r)}>Изменить</button>}
                    {!r.isSystem && <button disabled={r.assignments > 0} onClick={() => void remove(r)}>×</button>}
                  </div>
                </td>
              ))}
            </tr>
          </tbody>
        </table>
      </div>
      {edit && <RoleForm r={edit} onClose={() => setEdit(null)} onSaved={() => { setEdit(null); void load(); }} />}
    </div>
  );
}

function RoleForm({ r, onClose, onSaved }: { r: Partial<RoleRow>; onClose: () => void; onSaved: () => void }) {
  const [name, setName] = useState(r.name ?? '');
  const [description, setDescription] = useState(r.description ?? '');
  const [perms, setPerms] = useState<Set<Perm>>(new Set(r.permissions ?? []));
  const toggle = (p: Perm) => setPerms((s) => { const n = new Set(s); if (n.has(p)) n.delete(p); else n.add(p); return n; });
  const save = async () => {
    try {
      const body = { name, description, permissions: [...perms] };
      if (r.id) await api.put(`/api/roles/${r.id}`, body); else await api.post('/api/roles', body);
      toast.ok('Роль сохранена — права пользователей с этой ролью изменились сразу');
      onSaved();
    } catch (e) { toast.error(errorText(e)); }
  };
  const risky = perms.has('users.manage') && (perms.has('cmd.control') || perms.has('cmd.critical') || perms.has('config.publish'));
  return (
    <Modal title={r.id ? `Роль «${r.name}»` : 'Новая роль'} onClose={onClose}
      footer={<><button onClick={onClose}>Отмена</button><button className="primary" disabled={!name.trim()} onClick={save}>Сохранить</button></>}>
      <div className="form">
        <Field label="Название"><input id="role-name" value={name} onChange={(e) => setName(e.target.value)} /></Field>
        <Field label="Описание" wide><input value={description} onChange={(e) => setDescription(e.target.value)} /></Field>
      </div>
      <div className="perm-list">
        {PERMS.map((p) => (
          <label key={p.code} className="perm-item">
            <input type="checkbox" checked={perms.has(p.code)} onChange={() => toggle(p.code)} />
            <span><b>{p.name}</b>{p.rootOnly && <span className="muted"> · только на корне</span>}<div className="muted small">{p.hint}</div></span>
          </label>
        ))}
      </div>
      {risky && <div className="alert alert-warn">Роль совмещает управление доступом с управлением оборудованием или публикацией — это нарушает разделение обязанностей.</div>}
    </Modal>
  );
}

// ------------------------------------------------------------------ области
export function AreasPage() {
  const areas = useAreas((s) => s.areas);
  const reload = useAreas((s) => s.reload);
  const canUsers = useCan('users.manage');
  const canEdit = useCan('config.edit');
  const [edit, setEdit] = useState<Partial<Area> | null>(null);
  const tree = useMemo(() => areaTree(areas), [areas]);
  useEffect(() => { void reload(); }, [reload]);
  const remove = async (a: Area) => {
    if (!(await askConfirm(`Удалить область «${a.path}»?`, { ok: 'Удалить', danger: true }))) return;
    try { await api.del(`/api/areas/${a.id}`); void reload(); } catch (e) { toast.error(errorText(e)); }
  };
  const editable = canUsers || canEdit;
  return (
    <div className="page">
      <AccessTabs />
      <div className="toolbar">
        <h3>Области — дерево объектов</h3>
        <span className="muted small">Установка → проект → площадка → объект. Назначение роли действует на область и все вложенные.</span>
        <span className="grow" />
        {editable && <button className="primary" onClick={() => setEdit({ name: '', parentId: 1, sortOrder: 0 })}>Добавить область</button>}
      </div>
      <table className="table">
        <thead><tr><th>Область</th><th>Устройств</th><th>Тегов</th><th>Схем</th><th /></tr></thead>
        <tbody>
          {tree.map(({ area, depth }) => (
            <tr key={area.id}>
              <td style={{ paddingLeft: 12 + depth * 22 }}>{depth > 0 && <span className="muted">└ </span>}<b>{area.name}</b></td>
              <td>{area.devices}</td><td>{area.tags}</td><td>{area.mnemos}</td>
              <td className="actions">
                {editable && <button onClick={() => setEdit({ name: '', parentId: area.id, sortOrder: 0 })}>+ вложенная</button>}
                {editable && <button onClick={() => setEdit(area)}>Изменить</button>}
                {editable && area.parentId !== null && <button onClick={() => void remove(area)}>×</button>}
              </td>
            </tr>
          ))}
        </tbody>
      </table>
      <p className="muted small">
        Принадлежность устройств, тегов и схем к области меняется в их формах и вступает в силу после публикации.
        Тег без своей области принадлежит области устройства, тревога — области тега.
      </p>
      {edit && <AreaForm a={edit} onClose={() => setEdit(null)} onSaved={() => { setEdit(null); void reload(); }} />}
    </div>
  );
}

function AreaForm({ a, onClose, onSaved }: { a: Partial<Area>; onClose: () => void; onSaved: () => void }) {
  const [name, setName] = useState(a.name ?? '');
  const [parentId, setParentId] = useState<number | null>(a.parentId ?? 1);
  const [sortOrder, setSortOrder] = useState(a.sortOrder ?? 0);
  const save = async () => {
    try {
      const body = { name, parentId, sortOrder };
      if (a.id) await api.put(`/api/areas/${a.id}`, body); else await api.post('/api/areas', body);
      toast.ok('Область сохранена');
      onSaved();
    } catch (e) { toast.error(errorText(e)); }
  };
  return (
    <Modal title={a.id ? `Область «${a.name}»` : 'Новая область'} onClose={onClose}
      footer={<><button onClick={onClose}>Отмена</button><button className="primary" disabled={!name.trim()} onClick={save}>Сохранить</button></>}>
      <div className="form">
        <Field label="Название"><input id="area-name" value={name} onChange={(e) => setName(e.target.value)} /></Field>
        {a.parentId !== null && (
          <Field label="Входит в"><AreaSelect value={parentId} onChange={setParentId} /></Field>
        )}
        <Field label="Порядок"><input type="number" value={sortOrder} onChange={(e) => setSortOrder(Number(e.target.value))} /></Field>
      </div>
    </Modal>
  );
}
