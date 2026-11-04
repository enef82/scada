import { useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { errorText } from '../lib/api';
import { useAuth } from '../lib/stores';
import { DEMO } from '../demo/flag';

const DEMO_USERS = [
  ['ivanov', 'Оператор'], ['petrova', 'Инженер'], ['admin', 'Администратор'], ['dispatcher', 'Наблюдатель'],
] as const;

export function LoginPage() {
  const login = useAuth((s) => s.login);
  const navigate = useNavigate();
  const [user, setUser] = useState('');
  const [password, setPassword] = useState('');
  const [error, setError] = useState('');
  const [busy, setBusy] = useState(false);

  const submit = async (e: React.FormEvent) => {
    e.preventDefault();
    setBusy(true);
    setError('');
    try {
      await login(user, password);
      navigate('/');
    } catch (err) {
      setError(errorText(err));
    } finally {
      setBusy(false);
    }
  };

  return (
    <div className="login">
      <form className="login-box" onSubmit={submit}>
        <h1>SCADA</h1>
        <p className="muted">Диспетчерская система</p>
        <input autoFocus placeholder="Логин" value={user} onChange={(e) => setUser(e.target.value)} autoComplete="username" />
        <input type="password" placeholder="Пароль" value={password} onChange={(e) => setPassword(e.target.value)}
          autoComplete="current-password" />
        {error && <div className="alert alert-bad">{error}</div>}
        <button className="primary" disabled={busy || !user}>{busy ? 'Вход…' : 'Войти'}</button>
        {DEMO && (
          <div className="demo-logins">
            <span className="muted small">Прототип: войдите под любой ролью, пароль не нужен</span>
            <div className="demo-logins-row">
              {DEMO_USERS.map(([l, r]) => (
                <button type="button" key={l} onClick={() => { setUser(l); setPassword('demo'); void login(l, 'demo').then(() => navigate('/')); }}>
                  {r}
                </button>
              ))}
            </div>
          </div>
        )}
      </form>
    </div>
  );
}
