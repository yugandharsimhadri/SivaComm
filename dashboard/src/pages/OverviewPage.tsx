import { useEffect, useState } from 'react';
import { api, when, type ErrorRow, type Summary } from '../api';

const iso = (d: Date) => d.toISOString().slice(0, 10);

export function OverviewPage() {
  const [from, setFrom] = useState(iso(new Date(Date.now() - 29 * 86_400_000)));
  const [to, setTo] = useState(iso(new Date()));
  const [appId, setAppId] = useState('');
  const [summary, setSummary] = useState<Summary | null>(null);
  const [errors, setErrors] = useState<ErrorRow[]>([]);
  const [failed, setFailed] = useState<string | null>(null);

  useEffect(() => {
    // `to` is inclusive on screen, exclusive on the server.
    const end = new Date(to);
    end.setDate(end.getDate() + 1);
    const q = new URLSearchParams({ from, to: iso(end) });
    if (appId) q.set('applicationId', appId);

    Promise.all([api<Summary>(`/api/admin/summary?${q}`), api<ErrorRow[]>('/api/admin/errors')])
      .then(([s, e]) => {
        setSummary(s);
        setErrors(e);
        setFailed(null);
      })
      .catch((e) => setFailed(e.message));
  }, [from, to, appId]);

  const [allApps, setAllApps] = useState<{ id: string; name: string }[]>([]);
  useEffect(() => {
    api<{ id: string; name: string }[]>('/api/admin/applications').then(setAllApps).catch(() => undefined);
  }, []);

  const totals = summary?.applications.reduce(
    (t, a) => ({
      messages: t.messages + a.messagesTotal,
      created: t.created + a.challengesCreated,
      verified: t.verified + a.challengesVerified,
      failures: t.failures + a.verificationFailures,
      resets: t.resets + a.resetsCompleted,
      welcome: t.welcome + a.welcomeMessages,
      deliveryFailures: t.deliveryFailures + a.deliveryFailures,
    }),
    { messages: 0, created: 0, verified: 0, failures: 0, resets: 0, welcome: 0, deliveryFailures: 0 },
  );

  const workerStale = summary?.workerHeartbeatAt
    ? Date.now() - new Date(summary.workerHeartbeatAt).getTime() > 120_000
    : true;

  return (
    <>
      <div className="filters">
        <label>From <input type="date" value={from} onChange={(e) => setFrom(e.target.value)} /></label>
        <label>To <input type="date" value={to} onChange={(e) => setTo(e.target.value)} /></label>
        <label>
          Application
          <select value={appId} onChange={(e) => setAppId(e.target.value)}>
            <option value="">All</option>
            {allApps.map((a) => <option key={a.id} value={a.id}>{a.name}</option>)}
          </select>
        </label>
      </div>

      {failed && <p className="error">{failed}</p>}

      {summary && totals && (
        <>
          <section className="tiles">
            <Tile label="Messages" value={totals.messages} />
            <Tile label="OTPs created" value={totals.created} />
            <Tile label="OTPs verified" value={totals.verified} />
            <Tile label="Verification failures" value={totals.failures} tone={totals.failures ? 'warn' : undefined} />
            <Tile label="Resets completed" value={totals.resets} hint="as reported by source apps" />
            <Tile label="Welcome messages" value={totals.welcome} />
            <Tile label="Delivery failures" value={totals.deliveryFailures} tone={totals.deliveryFailures ? 'bad' : undefined} />
            <Tile label="Queue backlog" value={summary.queueBacklog} tone={summary.queueBacklog > 20 ? 'warn' : undefined}
              hint={summary.oldestQueuedAt ? `oldest ${when(summary.oldestQueuedAt)}` : 'empty'} />
          </section>

          <section className="card">
            <h2>Provider health</h2>
            <dl className="health">
              <dt>Worker</dt>
              <dd className={workerStale ? 'bad' : 'good'}>{workerStale ? 'not running' : `running (${when(summary.workerHeartbeatAt)})`}</dd>
              <dt>Last accepted by provider</dt>
              <dd>{when(summary.lastProviderSuccessAt)}</dd>
              <dt>Last failure</dt>
              <dd>{when(summary.lastProviderFailureAt)} {summary.lastProviderError && <code>{summary.lastProviderError}</code>}</dd>
            </dl>
          </section>

          <section className="card">
            <h2>By application</h2>
            <div className="scroll">
              <table>
                <thead>
                  <tr>
                    <th>Application</th><th>Messages</th><th>Queued</th><th>Submitted</th><th>Sent</th><th>Delivered</th>
                    <th>Read</th><th>Failed</th><th>OTPs</th><th>Verified</th><th>Failed checks</th><th>Resets done</th><th>Welcome</th>
                  </tr>
                </thead>
                <tbody>
                  {summary.applications.map((a) => (
                    <tr key={a.applicationId}>
                      <td>{a.name} <span className="muted">{a.code}</span></td>
                      <td>{a.messagesTotal}</td>
                      {['Queued', 'Submitted', 'Sent', 'Delivered', 'Read', 'Failed'].map((s) => <td key={s}>{a.messagesByStatus[s] ?? 0}</td>)}
                      <td>{a.challengesCreated}</td>
                      <td>{a.challengesVerified}</td>
                      <td>{a.verificationFailures}</td>
                      <td>{a.resetsCompleted}</td>
                      <td>{a.welcomeMessages}</td>
                    </tr>
                  ))}
                  {summary.applications.length === 0 && <tr><td colSpan={13} className="muted">No applications registered yet.</td></tr>}
                </tbody>
              </table>
            </div>
          </section>

          <section className="card">
            <h2>Recent errors</h2>
            {errors.length === 0 ? <p className="muted">None.</p> : (
              <div className="scroll">
                <table>
                  <thead><tr><th>When</th><th>Source</th><th>Application</th><th>Detail</th></tr></thead>
                  <tbody>
                    {errors.map((e, i) => (
                      <tr key={i}><td>{when(e.at)}</td><td>{e.source}</td><td>{e.application ?? '—'}</td><td><code>{e.detail}</code></td></tr>
                    ))}
                  </tbody>
                </table>
              </div>
            )}
          </section>
        </>
      )}
    </>
  );
}

function Tile({ label, value, hint, tone }: { label: string; value: number; hint?: string; tone?: 'warn' | 'bad' }) {
  return (
    <div className={`tile ${tone ?? ''}`}>
      <span className="label">{label}</span>
      <span className="value">{value.toLocaleString()}</span>
      {hint && <span className="hint">{hint}</span>}
    </div>
  );
}
