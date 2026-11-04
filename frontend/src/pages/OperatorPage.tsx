import { useEffect, useMemo, useState } from 'react';
import { NavLink, useParams } from 'react-router-dom';
import { Empty } from '../components/ui';
import { api, errorText } from '../lib/api';
import { useMeta } from '../lib/stores';
import type { MnemoInfo } from '../lib/types';
import { MnemoView } from '../mnemo/MnemoView';
import { normalizeDoc, type MnemoDoc } from '../mnemo/model';

/** Дерево мнемосхем (по parent_id). */
export function MnemoTree({ base = '/mnemo' }: { base?: string }) {
  const mnemos = useMeta((s) => s.mnemos);
  const children = useMemo(() => {
    const m = new Map<number | null, MnemoInfo[]>();
    for (const x of mnemos) {
      const k = x.parentId && mnemos.some((p) => p.id === x.parentId) ? x.parentId : null;
      m.set(k, [...(m.get(k) ?? []), x]);
    }
    return m;
  }, [mnemos]);

  const render = (parent: number | null, depth: number): React.ReactNode =>
    (children.get(parent) ?? []).map((m) => (
      <div key={m.id}>
        <NavLink to={`${base}/${m.id}`} className="tree-item" style={{ paddingLeft: 10 + depth * 14 }}>
          {children.has(m.id) ? '▾ ' : ''}{m.name}
        </NavLink>
        {render(m.id, depth + 1)}
      </div>
    ));

  return <div className="tree">{mnemos.length ? render(null, 0) : <Empty>Схем пока нет</Empty>}</div>;
}

export function OperatorPage() {
  const { id } = useParams();
  const mnemos = useMeta((s) => s.mnemos);
  const mnemoId = id ? Number(id) : mnemos[0]?.id;
  const [doc, setDoc] = useState<MnemoDoc | null>(null);
  const [error, setError] = useState('');
  const current = mnemos.find((m) => m.id === mnemoId);

  useEffect(() => {
    if (!mnemoId) return;
    let cancelled = false;
    setError('');
    api.get<{ doc: unknown; version: number }>(`/api/mnemos/${mnemoId}`)
      .then((r) => !cancelled && setDoc(r.doc ? normalizeDoc(r.doc) : null))
      .catch((e) => !cancelled && setError(errorText(e)));
    return () => { cancelled = true; };
    // перечитываем схему, если её сохранили (сменилась версия)
  }, [mnemoId, current?.currentVersion]);

  return (
    <div className="operator">
      <aside className="sidebar">
        <div className="sidebar-title">Объекты</div>
        <MnemoTree />
      </aside>
      <section className="operator-main">
        {error ? <Empty>{error}</Empty>
          : !mnemoId ? <Empty>Мнемосхем ещё нет. Инженер может создать их в разделе «Редактор схем».</Empty>
          : !doc ? <Empty>Схема «{current?.name}» пуста — откройте её в редакторе.</Empty>
          : <MnemoView doc={doc} />}
      </section>
    </div>
  );
}
