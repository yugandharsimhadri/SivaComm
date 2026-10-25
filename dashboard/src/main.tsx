import { StrictMode, useEffect, useState } from 'react';
import { createRoot } from 'react-dom/client';
import { api, post } from './api';
import { LoginPage } from './pages/LoginPage';
import { OverviewPage } from './pages/OverviewPage';
import { MessagesPage } from './pages/MessagesPage';
import { ApplicationsPage } from './pages/ApplicationsPage';
import './styles.css';

type Tab = 'overview' | 'messages' | 'applications';

function App() {
  const [user, setUser] = useState<string | null | undefined>(undefined);
  const [tab, setTab] = useState<Tab>('overview');

  useEffect(() => {
    api<{ username: string }>('/api/admin/auth/me')
      .then((me) => setUser(me.username))
      .catch(() => setUser(null));

    const signedOut = () => setUser(null);
    window.addEventListener('sivacomm:signed-out', signedOut);
    return () => window.removeEventListener('sivacomm:signed-out', signedOut);
  }, []);

  if (user === undefined) return null;
  if (user === null) return <LoginPage onSignedIn={setUser} />;

  const signOut = async () => {
    await post('/api/admin/auth/logout').catch(() => undefined);
    setUser(null);
  };

  return (
    <div className="shell">
      <header>
        <strong>SivaComm</strong>
        <nav>
          {(['overview', 'messages', 'applications'] as Tab[]).map((t) => (
            <button key={t} className={tab === t ? 'active' : ''} onClick={() => setTab(t)}>
              {t[0].toUpperCase() + t.slice(1)}
            </button>
          ))}
        </nav>
        <span className="who">
          {user} <button className="ghost" onClick={signOut}>Sign out</button>
        </span>
      </header>
      <main>
        {tab === 'overview' && <OverviewPage />}
        {tab === 'messages' && <MessagesPage />}
        {tab === 'applications' && <ApplicationsPage />}
      </main>
    </div>
  );
}

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <App />
  </StrictMode>,
);
