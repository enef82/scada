// Панель прототипа: задания для юзабилити-теста с автоматической проверкой и временем,
// сценарии отказов, смена роли и скорости модели. В рабочей сборке не подключается.
import { useEffect, useState } from 'react';
import { roleNames } from '../lib/format';
import { toast, useAuth } from '../lib/stores';
import type { Role } from '../lib/types';
import { marks, onMarks, procState, resetDemo, scenarios, setScenario, setSpeed, speed, switchRole, type Scenarios } from './sim';

interface Task { id: string; text: string; hint: string; check?: () => boolean }

const TASKS: Task[] = [
  { id: 'pressure', text: 'Найдите текущее давление в напорном коллекторе КНС-1', hint: 'Отметьте вручную, когда нашли' },
  { id: 'ackAll', text: 'Квитируйте все тревоги', hint: 'Проверяется автоматически', check: () => marks.has('ackAll') },
  { id: 'kns2', text: 'Выясните, с каким объектом нет связи и с какого времени', hint: 'Засчитывается при открытии схемы КНС-2 или диагностики', check: () => marks.has('kns2') },
  { id: 'manual', text: 'Переведите КНС-1 в ручной режим и запустите насос Н-2', hint: 'Проверяется автоматически', check: () => procState().auto === 0 && procState().p2 === 1 },
  { id: 'sp', text: 'Измените уставку уровня пуска на 2,5 м', hint: 'Проверяется автоматически', check: () => Math.abs(procState().sp - 2.5) < 0.001 },
  { id: 'trend', text: 'Постройте тренд уровня и давления КНС-1 за последний час', hint: 'Проверяется автоматически', check: () => marks.has('trend') },
  { id: 'shelve', text: 'Снимите тревогу «Открыта дверь КНС» с контроля на 1 час, указав причину', hint: 'Проверяется автоматически', check: () => marks.has('shelveDoor') },
  { id: 'lamp', text: 'Под ролью «Инженер» добавьте на схему КНС-1 лампу «Н-2 в работе» (тег KNS1_P2_Run), сохраните и опубликуйте — оператор должен её увидеть', hint: 'Засчитывается после публикации', check: () => marks.has('lamp') },
];

interface TestState { startedAt: number | null; done: Record<string, number>; notes: string }
const KEY = 'scada-demo-test';
const loadTest = (): TestState => {
  try { const s = localStorage.getItem(KEY); if (s) return JSON.parse(s) as TestState; } catch { /* нет */ }
  return { startedAt: null, done: {}, notes: '' };
};
const saveTest = (s: TestState) => { try { localStorage.setItem(KEY, JSON.stringify(s)); } catch { /* нет */ } };

const mmss = (sec: number) => `${Math.floor(sec / 60)}:${String(Math.floor(sec % 60)).padStart(2, '0')}`;

const SCENARIOS: { key: keyof Scenarios; on: string; off: string }[] = [
  { key: 'p1Fault', on: 'Авария насоса Н-1', off: 'Сбросить аварию Н-1' },
  { key: 'overheat', on: 'Перегрев обмотки Н-1', off: 'Нормальное охлаждение Н-1' },
  { key: 'link1Lost', on: 'Обрыв связи с КНС-1', off: 'Восстановить связь с КНС-1' },
  { key: 'kns2Online', on: 'Восстановить связь с КНС-2', off: 'Обрыв связи с КНС-2' },
  { key: 'door', on: 'Открыть дверь КНС-1', off: 'Закрыть дверь КНС-1' },
];

export default function DemoPanel() {
  const [open, setOpen] = useState(() => { try { return localStorage.getItem('scada-demo-panel') !== '0'; } catch { return true; } });
  const [tab, setTab] = useState<'tasks' | 'scenarios'>('tasks');
  const [test, setTest] = useState<TestState>(loadTest);
  const [, force] = useState(0);
  const [now, setNow] = useState(Date.now());
  const me = useAuth((s) => s.me);

  useEffect(() => {
    try { localStorage.setItem('scada-demo-panel', open ? '1' : '0'); } catch { /* нет */ }
    document.body.classList.toggle('demo-docked', open);
    window.dispatchEvent(new Event('resize')); // графики подстраиваются под новую ширину
  }, [open]);
  useEffect(() => saveTest(test), [test]);

  // автоматическая проверка заданий
  useEffect(() => {
    const check = () => setTest((t) => {
      if (!t.startedAt) return t;
      let changed = false;
      const done = { ...t.done };
      for (const task of TASKS) {
        if (task.check && done[task.id] === undefined && task.check()) {
          done[task.id] = (Date.now() - t.startedAt) / 1000;
          changed = true;
          toast.ok(`Задание выполнено: ${task.text}`);
        }
      }
      return changed ? { ...t, done } : t;
    });
    const off = onMarks(check);
    const timer = setInterval(() => { check(); setNow(Date.now()); }, 1000);
    return () => { off(); clearInterval(timer); };
  }, []);

  const start = () => { marks.clear(); setTest({ startedAt: Date.now(), done: {}, notes: test.notes }); };
  const finished = Object.keys(test.done).length;
  const elapsed = test.startedAt ? (now - test.startedAt) / 1000 : 0;

  const copy = async () => {
    const lines = [
      `Оценка интерфейса SCADA — ${new Date().toLocaleString('ru-RU')}`,
      `Выполнено заданий: ${finished} из ${TASKS.length}; общее время ${mmss(elapsed)}`,
      ...TASKS.map((t, i) => `${i + 1}. ${t.text} — ${test.done[t.id] !== undefined ? mmss(test.done[t.id]) : 'не выполнено'}`),
      test.notes ? `\nЗамечания:\n${test.notes}` : '',
    ];
    try { await navigator.clipboard.writeText(lines.join('\n')); toast.ok('Результаты скопированы'); }
    catch { toast.error('Браузер не дал доступ к буферу обмена — выделите текст замечаний вручную'); }
  };

  const role = (r: Role) => {
    const m = switchRole(r);
    useAuth.setState({ me: m, loaded: true });
    toast.info(`Роль: ${roleNames[r]}`);
  };

  if (!open) {
    return (
      <button className="demo-fab" onClick={() => setOpen(true)} aria-label="Открыть панель прототипа">
        Прототип · {test.startedAt ? `${finished}/${TASKS.length}` : 'задания'}
      </button>
    );
  }

  return (
    <aside className="demo-panel" aria-label="Панель прототипа">
      <div className="demo-head">
        <b>Прототип интерфейса</b>
        <span className="muted small">модель КНС в браузере</span>
        <button className="icon-btn" onClick={() => setOpen(false)} aria-label="Свернуть">–</button>
      </div>

      <div className="demo-section">
        <div className="demo-label">Роль</div>
        <div className="demo-seg">
          {(['viewer', 'operator', 'engineer', 'admin'] as Role[]).map((r) => (
            <button key={r} className={me?.role === r ? 'active' : ''} onClick={() => role(r)}>{roleNames[r]}</button>
          ))}
        </div>
      </div>

      <div className="tabs demo-tabs">
        <button className={tab === 'tasks' ? 'active' : ''} onClick={() => setTab('tasks')}>Задания</button>
        <button className={tab === 'scenarios' ? 'active' : ''} onClick={() => setTab('scenarios')}>Сценарии</button>
      </div>

      {tab === 'tasks' ? (
        <div className="demo-section">
          <div className="demo-row">
            {test.startedAt
              ? <span className="small">Выполнено <b>{finished}</b> из {TASKS.length} · <span className="mono">{mmss(elapsed)}</span></span>
              : <span className="small muted">Время по каждому заданию считается от старта</span>}
            <span className="grow" />
            <button className={test.startedAt ? '' : 'primary'} onClick={start}>{test.startedAt ? 'Заново' : 'Начать тест'}</button>
          </div>
          <ol className="demo-tasks">
            {TASKS.map((t) => {
              const d = test.done[t.id];
              return (
                <li key={t.id} className={d !== undefined ? 'done' : ''}>
                  <div>{t.text}</div>
                  <div className="demo-task-meta">
                    {d !== undefined ? <span className="demo-ok">✓ {mmss(d)}</span> : <span className="muted">{t.hint}</span>}
                    {!t.check && test.startedAt && d === undefined && (
                      <button onClick={() => setTest((s) => ({ ...s, done: { ...s.done, [t.id]: (Date.now() - s.startedAt!) / 1000 } }))}>Нашёл</button>
                    )}
                  </div>
                </li>
              );
            })}
          </ol>
          <label className="demo-label" htmlFor="demo-notes">Замечания по интерфейсу</label>
          <textarea id="demo-notes" rows={3} value={test.notes} placeholder="Что было непонятно, где искали дольше всего, чего не хватило"
            onChange={(e) => setTest((s) => ({ ...s, notes: e.target.value }))} />
          <button onClick={() => void copy()}>Скопировать результаты</button>
        </div>
      ) : (
        <div className="demo-section">
          <p className="small muted">События процесса для проверки отображения тревог, потери связи и реакции оператора.</p>
          <div className="demo-scen">
            {SCENARIOS.map((s) => (
              <button key={s.key}
                onClick={() => { setScenario(s.key, !scenarios[s.key]); force((x) => x + 1); }}>
                {scenarios[s.key] ? s.off : s.on}
              </button>
            ))}
          </div>
          <div className="demo-label">Скорость модели</div>
          <div className="demo-seg demo-seg-3">
            {[1, 5, 20].map((x) => (
              <button key={x} className={speed === x ? 'active' : ''} onClick={() => { setSpeed(x); force((v) => v + 1); }}>×{x}</button>
            ))}
          </div>
          <button onClick={() => { resetDemo(); setTest({ startedAt: null, done: {}, notes: '' }); force((x) => x + 1); toast.info('Прототип сброшен к исходному состоянию'); }}>
            Сбросить схемы, настройки и тест
          </button>
        </div>
      )}
    </aside>
  );
}
