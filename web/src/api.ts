export type Role = 'system_admin' | 'company_admin' | 'project_manager' | 'executor' | 'viewer';

export interface Project { id: string; code: string | null; name: string; title: string | null }
export interface Me { userId: string; name: string; role: Role; organizationId: string | null }
export interface Task {
  id: string; parent_id: string | null; organization_id: string | null; sort_order: number; wbs: string | null;
  name: string; is_summary: boolean; start_date: string | null; finish_date: string | null;
  duration_days: number | null; percent_complete: number | null; priority: string | null;
  assignee_text: string | null; predecessors: string | null; notes: string | null; source_info: string | null;
  version: number; headingOnly?: boolean;
}
export interface Ancestor { id: string; parent_id: string | null; wbs: string | null; name: string; sort_order: number }
export interface Notification { id: string; task_id: string | null; kind: string; message: string; read_at: string | null; created_at: string }
export interface Summary { tasks: number; notStarted: number; inProgress: number; completed: number; overdue: number; withoutDates: number }
export interface Organization { id: string; name: string }
export interface Member { userId: string; name: string; role: Role; organizationId: string | null }
export interface Category { id: string; parentId: string | null; name: string; sortOrder: number }
export interface DocumentItem {
  id: string; title: string; code: string | null; categoryId: string | null; taskId: string | null; organizationId: string | null; createdAt: string;
  latest: { id: string; versionNo: number; fileName: string; sizeBytes: number; uploadedAt: string };
}
export interface DocumentVersion { id: string; versionNo: number; fileName: string; sizeBytes: number; sha256: string; uploadedBy: string; uploadedAt: string }

export class ApiError extends Error {
  constructor(public status: number, message: string) { super(message); }
}

export const getToken = () => {
  try { return sessionStorage.getItem('noor-token'); } catch { return null; }
};

const headers = (): Record<string, string> => {
  const token = getToken();
  return token ? { Authorization: `Bearer ${token}` } : {};
};

async function failure(response: Response) {
  let message = 'ارتباط با سرور برقرار نشد.';
  try { message = (await response.json()).error ?? message; } catch { /* non-JSON response */ }
  if (response.status === 401) message = 'نشست شما تمام شده است. دوباره وارد شوید.';
  return new ApiError(response.status, message);
}

export async function api<T>(path: string, method = 'GET', body?: unknown): Promise<T> {
  const response = await fetch(`/api${path}`, {
    method,
    headers: { ...headers(), ...(body === undefined ? {} : { 'Content-Type': 'application/json' }) },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  if (!response.ok) throw await failure(response);
  return (response.status === 204 ? undefined : await response.json()) as T;
}

export function upload(path: string, form: FormData, onProgress: (fraction: number) => void) {
  return new Promise<void>((resolve, reject) => {
    const xhr = new XMLHttpRequest();
    xhr.open('POST', `/api${path}`);
    Object.entries(headers()).forEach(([k, v]) => xhr.setRequestHeader(k, v));
    xhr.upload.onprogress = e => e.lengthComputable && onProgress(e.loaded / e.total);
    xhr.onload = () => {
      if (xhr.status < 300) return resolve();
      let message = 'بارگذاری انجام نشد.';
      try { message = JSON.parse(xhr.responseText).error ?? message; } catch { /* keep default */ }
      reject(new ApiError(xhr.status, message));
    };
    xhr.onerror = () => reject(new ApiError(0, 'ارتباط با سرور قطع شد.'));
    xhr.send(form);
  });
}

export async function download(path: string, fileName: string) {
  const response = await fetch(`/api${path}`, { headers: headers() });
  if (!response.ok) throw await failure(response);
  const url = URL.createObjectURL(await response.blob());
  const link = document.createElement('a');
  link.href = url;
  link.download = fileName;
  link.click();
  setTimeout(() => URL.revokeObjectURL(url), 30000);
}
