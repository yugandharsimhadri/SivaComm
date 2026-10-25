/**
 * The dashboard's only way to the API. Same origin, cookie session — there is
 * no token anywhere in browser storage, and no application credential ever
 * reaches this code.
 *
 * Every request carries X-SivaComm-Admin: the API refuses state-changing
 * admin calls without it, and a browser will not add a custom header to a
 * cross-site request, so another site cannot drive an operator's session.
 */
export class ApiError extends Error {
  constructor(message: string, public status: number) {
    super(message);
  }
}

export async function api<T>(path: string, init: RequestInit = {}): Promise<T> {
  const response = await fetch(path, {
    ...init,
    credentials: 'same-origin',
    headers: {
      'X-SivaComm-Admin': '1',
      ...(init.body ? { 'Content-Type': 'application/json' } : {}),
      ...init.headers,
    },
  });

  if (response.status === 401 && !path.endsWith('/login')) {
    window.dispatchEvent(new Event('sivacomm:signed-out'));
  }

  if (!response.ok) {
    let message = response.statusText || 'Request failed';
    try {
      const body = await response.json();
      message = body.message ?? message;
    } catch {
      // Not JSON; keep the status text.
    }
    throw new ApiError(message, response.status);
  }

  return response.status === 204 ? (undefined as T) : ((await response.json()) as T);
}

export const post = <T>(path: string, body?: unknown) =>
  api<T>(path, { method: 'POST', body: body === undefined ? undefined : JSON.stringify(body) });

export const put = <T>(path: string, body: unknown) => api<T>(path, { method: 'PUT', body: JSON.stringify(body) });

// ── Shapes returned by /api/admin ──────────────────────────────────────────

export interface AppSummary {
  applicationId: string;
  code: string;
  name: string;
  messagesTotal: number;
  messagesByStatus: Record<string, number>;
  welcomeMessages: number;
  otpMessages: number;
  challengesCreated: number;
  challengesVerified: number;
  verificationFailures: number;
  resetsCompleted: number;
  deliveryFailures: number;
}

export interface Summary {
  from: string;
  to: string;
  applications: AppSummary[];
  queueBacklog: number;
  oldestQueuedAt: string | null;
  lastProviderSuccessAt: string | null;
  lastProviderFailureAt: string | null;
  lastProviderError: string | null;
  workerHeartbeatAt: string | null;
}

export interface MessageRow {
  id: string;
  application: string;
  purpose: 'PasswordResetOtp' | 'Welcome';
  status: string;
  recipient: string;
  template: string;
  attempts: number;
  lastError: string | null;
  challengeStatus: string | null;
  createdAt: string;
  submittedAt: string | null;
  deliveredAt: string | null;
  readAt: string | null;
  failedAt: string | null;
}

export interface Page<T> {
  items: T[];
  total: number;
  pageNumber: number;
  pageSize: number;
}

export interface ErrorRow {
  at: string;
  source: string;
  application: string | null;
  detail: string;
}

export interface KeyView {
  keyId: string;
  createdAt: string;
  revokedAt: string | null;
  lastUsedAt: string | null;
}

export interface TemplateView {
  purpose: 'PasswordResetOtp' | 'Welcome';
  templateName: string;
  languageCode: string;
  parameterCount: number;
}

export interface ApplicationView {
  id: string;
  code: string;
  name: string;
  isActive: boolean;
  createdAt: string;
  keys: KeyView[];
  templates: TemplateView[];
}

export interface IssuedKey {
  clientId: string;
  keyId: string;
  secret: string;
}

export const when = (iso: string | null) => (iso ? new Date(iso).toLocaleString() : '—');
