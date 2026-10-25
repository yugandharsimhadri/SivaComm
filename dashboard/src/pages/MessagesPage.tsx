import { useEffect, useState } from 'react';
import { api, when, type MessageRow, type Page } from '../api';

const STATUSES = ['Queued', 'Submitted', 'Sent', 'Delivered', 'Read', 'Failed', 'Expired'];

/**
 * Message history. Delivery status and OTP state are separate columns on
 * purpose: "Delivered" says the phone has the code, not that anyone used it.
 * Phone numbers arrive from the API already masked.
 */
export function MessagesPage() {
  const [apps, setApps] = useState<{ id: string; name: string }[]>([]);
  const [appId, setAppId] = useState('');
  const [status, setStatus] = useState('');
  const [purpose, setPurpose] = useState('');
  const [page, setPage] = useState(1);
  const [data, setData] = useState<Page<MessageRow> | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    api<{ id: string; name: string }[]>('/api/admin/applications').then(setApps).catch(() => undefined);
  }, []);

  useEffect(() => {
    const q = new URLSearchParams({ page: String(page), pageSize: '25' });
    if (appId) q.set('applicationId', appId);
    if (status) q.set('status', status);
    if (purpose) q.set('purpose', purpose);
    api<Page<MessageRow>>(`/api/admin/messages?${q}`).then(setData).catch((e) => setError(e.message));
  }, [appId, status, purpose, page]);

  const pages = data ? Math.max(1, Math.ceil(data.total / data.pageSize)) : 1;
  const filter = (set: (v: string) => void) => (v: string) => { set(v); setPage(1); };

  return (
    <>
      <div className="filters">
        <label>
          Application
          <select value={appId} onChange={(e) => filter(setAppId)(e.target.value)}>
            <option value="">All</option>
            {apps.map((a) => <option key={a.id} value={a.id}>{a.name}</option>)}
          </select>
        </label>
        <label>
          Status
          <select value={status} onChange={(e) => filter(setStatus)(e.target.value)}>
            <option value="">All</option>
            {STATUSES.map((s) => <option key={s}>{s}</option>)}
          </select>
        </label>
        <label>
          Kind
          <select value={purpose} onChange={(e) => filter(setPurpose)(e.target.value)}>
            <option value="">All</option>
            <option value="PasswordResetOtp">Password-reset OTP</option>
            <option value="Welcome">Welcome</option>
          </select>
        </label>
      </div>

      {error && <p className="error">{error}</p>}

      <section className="card">
        <div className="scroll">
          <table>
            <thead>
              <tr>
                <th>Created</th><th>Application</th><th>Kind</th><th>To</th><th>Delivery</th><th>OTP state</th>
                <th>Attempts</th><th>Delivered</th><th>Error</th>
              </tr>
            </thead>
            <tbody>
              {data?.items.map((m) => (
                <tr key={m.id}>
                  <td>{when(m.createdAt)}</td>
                  <td>{m.application}</td>
                  <td>{m.purpose === 'Welcome' ? 'Welcome' : 'OTP'}</td>
                  <td><code>{m.recipient}</code></td>
                  <td><span className={`badge ${m.status.toLowerCase()}`}>{m.status}</span></td>
                  <td>{m.challengeStatus ?? '—'}</td>
                  <td>{m.attempts}</td>
                  <td>{when(m.deliveredAt)}</td>
                  <td>{m.lastError ? <code>{m.lastError}</code> : '—'}</td>
                </tr>
              ))}
              {data?.items.length === 0 && <tr><td colSpan={9} className="muted">No messages match.</td></tr>}
            </tbody>
          </table>
        </div>
        <div className="pager">
          <button disabled={page <= 1} onClick={() => setPage(page - 1)}>Previous</button>
          <span>Page {page} of {pages} · {data?.total ?? 0} messages</span>
          <button disabled={page >= pages} onClick={() => setPage(page + 1)}>Next</button>
        </div>
      </section>
    </>
  );
}
