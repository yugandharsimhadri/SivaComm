import { useEffect, useState, type FormEvent } from 'react';
import { api, post, put, when, ApiError, type ApplicationView, type IssuedKey, type TemplateView } from '../api';

/**
 * Registering applications and rotating their keys. A new secret is shown
 * exactly once, here, and is then gone — the API stores it only encrypted
 * and has no endpoint that returns it again.
 */
export function ApplicationsPage() {
  const [apps, setApps] = useState<ApplicationView[]>([]);
  const [issued, setIssued] = useState<IssuedKey | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [code, setCode] = useState('');
  const [name, setName] = useState('');

  const load = () => api<ApplicationView[]>('/api/admin/applications').then(setApps).catch((e) => setError(e.message));
  useEffect(() => { load(); }, []);

  const run = async (action: () => Promise<unknown>) => {
    setError(null);
    try {
      await action();
      await load();
    } catch (e) {
      setError(e instanceof ApiError ? e.message : 'That did not work.');
    }
  };

  const register = (e: FormEvent) => {
    e.preventDefault();
    run(async () => {
      const r = await post<{ key: IssuedKey }>('/api/admin/applications', { code: code.trim(), name: name.trim() });
      setIssued(r.key);
      setCode('');
      setName('');
    });
  };

  return (
    <>
      {issued && (
        <section className="card secret">
          <h2>Copy this secret now</h2>
          <p>It will not be shown again. Put it in the application's server configuration — never in a browser bundle.</p>
          <dl className="health">
            <dt>Client</dt><dd><code>{issued.clientId}</code></dd>
            <dt>Key id</dt><dd><code>{issued.keyId}</code></dd>
            <dt>Secret</dt><dd><code className="select">{issued.secret}</code></dd>
          </dl>
          <button onClick={() => setIssued(null)}>I have stored it</button>
        </section>
      )}

      {error && <p className="error">{error}</p>}

      {apps.map((a) => (
        <section className="card" key={a.id}>
          <div className="row">
            <h2>{a.name} <span className="muted">{a.code}</span></h2>
            <span className={`badge ${a.isActive ? 'delivered' : 'failed'}`}>{a.isActive ? 'Active' : 'Disabled'}</span>
            <button className="ghost" onClick={() => run(() => put(`/api/admin/applications/${a.id}/active`, { active: !a.isActive }))}>
              {a.isActive ? 'Disable' : 'Enable'}
            </button>
          </div>

          <h3>Keys</h3>
          <table>
            <thead><tr><th>Key id</th><th>Created</th><th>Last used</th><th>Status</th><th /></tr></thead>
            <tbody>
              {a.keys.map((k) => (
                <tr key={k.keyId}>
                  <td><code>{k.keyId}</code></td>
                  <td>{when(k.createdAt)}</td>
                  <td>{when(k.lastUsedAt)}</td>
                  <td>{k.revokedAt ? `revoked ${when(k.revokedAt)}` : 'live'}</td>
                  <td>
                    {!k.revokedAt && (
                      <button className="ghost danger" onClick={() => {
                        if (confirm(`Revoke ${k.keyId}? ${a.name} stops working until it uses another live key.`))
                          run(() => post(`/api/admin/applications/${a.id}/keys/${k.keyId}/revoke`));
                      }}>Revoke</button>
                    )}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
          <button onClick={() => run(async () => setIssued(await post<IssuedKey>(`/api/admin/applications/${a.id}/keys`)))}>
            Issue a second key (rotation)
          </button>

          <h3>Templates</h3>
          <TemplateEditor appId={a.id} purpose="PasswordResetOtp" current={a.templates.find((t) => t.purpose === 'PasswordResetOtp')} onSaved={load} />
          <TemplateEditor appId={a.id} purpose="Welcome" current={a.templates.find((t) => t.purpose === 'Welcome')} onSaved={load} />
        </section>
      ))}

      <section className="card">
        <h2>Register an application</h2>
        <form onSubmit={register} className="inline">
          <label>Code <input value={code} onChange={(e) => setCode(e.target.value)} placeholder="hms-web" pattern="[a-z0-9][a-z0-9-]{1,39}" required /></label>
          <label>Name <input value={name} onChange={(e) => setName(e.target.value)} placeholder="Sivayaan HMS" required /></label>
          <button className="primary">Register</button>
        </form>
      </section>
    </>
  );
}

function TemplateEditor({ appId, purpose, current, onSaved }: {
  appId: string; purpose: TemplateView['purpose']; current?: TemplateView; onSaved: () => void;
}) {
  const [templateName, setTemplateName] = useState(current?.templateName ?? '');
  const [languageCode, setLanguageCode] = useState(current?.languageCode ?? 'en');
  const [parameterCount, setParameterCount] = useState(current?.parameterCount ?? (purpose === 'Welcome' ? 2 : 1));
  const [state, setState] = useState<string | null>(null);

  const save = async (e: FormEvent) => {
    e.preventDefault();
    try {
      await put(`/api/admin/applications/${appId}/templates`, { purpose, templateName, languageCode, parameterCount });
      setState('Saved');
      onSaved();
    } catch (err) {
      setState(err instanceof ApiError ? err.message : 'Could not save');
    }
  };

  return (
    <form onSubmit={save} className="inline">
      <strong className="purpose">{purpose === 'Welcome' ? 'Welcome' : 'Password-reset OTP'}</strong>
      <label>Approved template <input value={templateName} onChange={(e) => setTemplateName(e.target.value)} required /></label>
      <label>Language <input value={languageCode} onChange={(e) => setLanguageCode(e.target.value)} size={6} required /></label>
      {purpose === 'Welcome' ? (
        <label>Parameters <input type="number" min={0} max={10} value={parameterCount} onChange={(e) => setParameterCount(Number(e.target.value))} /></label>
      ) : (
        <span className="muted">Authentication template · 1 parameter (the code)</span>
      )}
      <button>Save</button>
      {state && <span className="muted">{state}</span>}
    </form>
  );
}
