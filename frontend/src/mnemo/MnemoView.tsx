import { useMemo, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { Modal } from '../components/ui';
import { requestCommand } from '../components/CommandDialog';
import { useAlarms } from '../lib/alarms';
import { formatValue, qualityCategory, qualityName } from '../lib/format';
import { useTagSubscription, useTagValue } from '../lib/realtime';
import { useCan, useMeta } from '../lib/stores';
import { ElementView, SvgDefs, type RenderCtx } from './elements';
import { boundTagNames, str, type MnemoDoc, type MnemoElement } from './model';

/** Исполнение мнемосхемы (АРМ оператора). */
export function MnemoView({ doc, subscriptionKey = 'mnemo' }: { doc: MnemoDoc; subscriptionKey?: string }) {
  const navigate = useNavigate();
  const canOperate = useCan('operator');
  const tagByName = useMeta((s) => s.tagByName);
  const [faceplate, setFaceplate] = useState<MnemoElement | null>(null);

  const ids = useMemo(
    () => boundTagNames(doc).map((n) => tagByName.get(n)?.id).filter((x): x is number => x !== undefined),
    [doc, tagByName]);
  useTagSubscription(subscriptionKey, ids);

  const ctx: RenderCtx = useMemo(() => ({
    editing: false,
    canOperate,
    navigate: (id) => navigate(`/mnemo/${id}`),
    openFaceplate: setFaceplate,
  }), [canOperate, navigate]);

  return (
    <div className="mnemo-view" style={{ background: doc.background }}>
      <svg viewBox={`0 0 ${doc.width} ${doc.height}`} preserveAspectRatio="xMidYMid meet" className="mnemo-svg">
        <SvgDefs />
        <rect width={doc.width} height={doc.height} fill={doc.background} />
        {doc.elements.map((el) => <ElementView key={el.id} el={el} ctx={ctx} />)}
      </svg>
      {faceplate && <Faceplate el={faceplate} onClose={() => setFaceplate(null)} />}
    </div>
  );
}

function StateRow({ label, tagName }: { label: string; tagName: string }) {
  const tag = useMeta((s) => s.tagByName.get(tagName));
  const v = useTagValue(tag?.id);
  if (!tagName) return null;
  return (
    <div className="cmd-row">
      <span>{label}</span>
      <span className={`q-${qualityCategory(v?.[3])}`}>
        {tag ? formatValue(tag, v?.[1], v?.[2]) : 'тег не найден'} <small className="muted">{qualityName(v?.[3])}</small>
      </span>
    </div>
  );
}

/** Панель управления оборудованием (faceplate). */
function Faceplate({ el, onClose }: { el: MnemoElement; onClose: () => void }) {
  const navigate = useNavigate();
  const can = useCan('operator');
  const byName = useMeta((s) => s.tagByName);
  const cmd = byName.get(str(el.props.cmdTag));
  const state = byName.get(str(el.props.stateTag));
  const fault = byName.get(str(el.props.faultTag));
  const alarmsByTag = useAlarms((s) => s.byTag);
  const alarms = [state, fault].flatMap((t) => (t ? alarmsByTag.get(t.id) ?? [] : []));
  const isValve = el.type === 'valve';
  const title = `${str(el.props.label) || 'Оборудование'} — ${isValve ? 'задвижка' : el.type === 'pump' ? 'насос' : 'двигатель'}`;
  const trendTags = [state, fault].filter(Boolean).map((t) => t!.id).join(',');

  return (
    <Modal title={title} onClose={onClose}
      footer={<button onClick={() => { onClose(); navigate(`/trends?tags=${trendTags}`); }} disabled={!trendTags}>Тренд</button>}>
      <div className="cmd">
        <StateRow label={isValve ? 'Открыта' : 'Работа'} tagName={str(el.props.stateTag)} />
        <StateRow label="Авария" tagName={str(el.props.faultTag)} />
        {alarms.length > 0 && (
          <div className="alert alert-bad">
            {alarms.map((a) => <div key={a.id}>{a.message}{a.acked ? '' : ' (не квитирована)'}</div>)}
          </div>
        )}
        {cmd ? (
          <div className="faceplate-buttons">
            <button className="primary" disabled={!can || !cmd.writable}
              onClick={() => { requestCommand({ tag: cmd, value: 1, label: `${isValve ? 'Открыть' : 'Пуск'}: ${str(el.props.label)}` }); onClose(); }}>
              {isValve ? 'Открыть' : 'Пуск'}
            </button>
            <button disabled={!can || !cmd.writable}
              onClick={() => { requestCommand({ tag: cmd, value: 0, label: `${isValve ? 'Закрыть' : 'Стоп'}: ${str(el.props.label)}` }); onClose(); }}>
              {isValve ? 'Закрыть' : 'Стоп'}
            </button>
          </div>
        ) : (
          <p className="muted small">Тег команды не задан — только индикация.</p>
        )}
        {!can && <p className="muted small">Управление доступно операторам.</p>}
      </div>
    </Modal>
  );
}
