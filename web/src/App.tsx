import { useEffect, useMemo, useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Bell, BellOff, ChartColumn, FolderOpen, LayoutDashboard, ListTree, LogOut, Moon, ShieldCheck, Sun } from 'lucide-react';
import { signIn, signOut } from './auth';
import { api, getToken, type Ancestor, type Me, type Notification, type Project, type Task } from './api';
import { ROLE_LABEL, buildTree, isManager, longDate, todayIso, toFa } from './model';
import { Empty, Popover, Skeleton } from './ui';
import Overview from './pages/Overview';
import Tasks from './pages/Tasks';
import Library from './pages/Library';
import Reports from './pages/Reports';
import Access from './pages/Access';

type Page = 'overview' | 'tasks' | 'library' | 'reports' | 'access';
const NAV: { id: Page; label: string; icon: typeof ListTree; manager?: boolean }[] = [
  { id: 'overview', label: 'نمای کلی', icon: LayoutDashboard },
  { id: 'tasks', label: 'تسک‌ها', icon: ListTree },
  { id: 'library', label: 'کتابخانه مدارک', icon: FolderOpen },
  { id: 'reports', label: 'گزارش‌ها', icon: ChartColumn },
  { id: 'access', label: 'دسترسی‌ها', icon: ShieldCheck, manager: true },
];

const pageFromHash = (): Page => {
  const id = location.hash.slice(1).split('?')[0];
  return NAV.some(n => n.id === id) ? (id as Page) : 'overview';
};

export function useProjectData(projectId: string) {
  const tasks = useQuery({ queryKey: ['tasks', projectId], queryFn: () => api<Task[]>(`/projects/${projectId}/tasks`), refetchInterval: 20000 });
  const context = useQuery({ queryKey: ['context', projectId], queryFn: () => api<Ancestor[]>(`/projects/${projectId}/task-context`) });
  const tree = useMemo(() => buildTree(tasks.data ?? [], context.data ?? []), [tasks.data, context.data]);
  return { tree, loading: tasks.isLoading || context.isLoading, error: tasks.error ?? context.error };
}

function ThemeButton() {
  const [dark, setDark] = useState(() => document.documentElement.dataset.theme === 'dark');
  useEffect(() => {
    if (dark) document.documentElement.dataset.theme = 'dark'; else delete document.documentElement.dataset.theme;
    try { localStorage.setItem('noor-theme', dark ? 'dark' : 'light'); } catch { /* storage unavailable */ }
  }, [dark]);
  const label = dark ? 'تغییر به تم روشن' : 'تغییر به تم تیره';
  return <button className="icon-btn" onClick={() => setDark(!dark)} title={label} aria-label={label}>{dark ? <Sun size={19} /> : <Moon size={19} />}</button>;
}

function Notifications({ onOpenTask }: { onOpenTask: (id: string) => void }) {
  const client = useQueryClient();
  const [anchor, setAnchor] = useState<DOMRect | null>(null);
  const { data = [] } = useQuery({ queryKey: ['notifications'], queryFn: () => api<Notification[]>('/notifications'), refetchInterval: 30000 });
  const read = useMutation({ mutationFn: (id: string) => api(`/notifications/${id}/read`, 'POST'), onSuccess: () => client.invalidateQueries({ queryKey: ['notifications'] }) });
  const unread = data.filter(n => !n.read_at).length;
  return (
    <>
      <button className="icon-btn bell" onClick={e => setAnchor(e.currentTarget.getBoundingClientRect())} aria-label={`اعلان‌ها، ${toFa(unread)} خوانده‌نشده`}>
        <Bell size={19} />{unread > 0 && <em>{toFa(unread > 99 ? '۹۹+' : unread)}</em>}
      </button>
      {anchor && (
        <Popover anchor={anchor} onClose={() => setAnchor(null)}>
          <div className="notif-head"><strong>اعلان‌ها</strong>{unread > 0 && <span>{toFa(unread)} جدید</span>}</div>
          <div className="notif-list">
            {data.length === 0 && <Empty icon={<BellOff size={20} />} title="اعلانی ندارید" />}
            {data.slice(0, 30).map(n => (
              <button key={n.id} className={`notif ${n.read_at ? '' : 'new'} k-${n.kind}`} onClick={() => {
                if (!n.read_at) read.mutate(n.id);
                if (n.task_id) onOpenTask(n.task_id);
                setAnchor(null);
              }}><i />{n.message}</button>
            ))}
          </div>
        </Popover>
      )}
    </>
  );
}

export default function App({ authFailed }: { authFailed?: boolean }) {
  const [page, setPage] = useState<Page>(pageFromHash);
  const [focusTask, setFocusTask] = useState<string | null>(null);
  useEffect(() => {
    const change = () => setPage(pageFromHash());
    window.addEventListener('hashchange', change);
    return () => window.removeEventListener('hashchange', change);
  }, []);

  const signedIn = !!getToken();
  const projects = useQuery({ queryKey: ['projects'], queryFn: () => api<Project[]>('/projects'), enabled: signedIn });
  const project = projects.data?.[0];
  const me = useQuery({ queryKey: ['me', project?.id], queryFn: () => api<Me>(`/projects/${project!.id}/me`), enabled: !!project });

  if (authFailed) return <SignIn message="اتصال به سامانه ورود برقرار نشد. صفحه را دوباره باز کنید." />;
  if (!signedIn) return <SignIn />;
  if (projects.error) return <SignIn message={(projects.error as Error).message} />;
  if (projects.isLoading || (project && me.isLoading)) return <div className="boot"><div className="logo">ن</div><Skeleton rows={3} /></div>;
  if (!project || !me.data) return <div className="boot"><Empty icon={<ShieldCheck size={24} />} title="هنوز به پروژه‌ای دسترسی ندارید" text="ورود شما ثبت شد. از مدیر پروژه بخواهید شما را به پروژه اضافه کند." /><button className="btn" onClick={() => signOut()}>خروج</button></div>;

  const nav = NAV.filter(n => !n.manager || isManager(me.data.role));
  const current = nav.find(n => n.id === page) ?? nav[0];
  const go = (id: Page) => { location.hash = id; };
  const openTask = (id: string) => { setFocusTask(id); go('tasks'); };

  return (
    <div className="shell">
      <aside className="side">
        <div className="brand"><div className="logo">ن</div><div><strong>پروژه نور</strong><small>مدیریت یکپارچه پروژه</small></div></div>
        <nav aria-label="منوی اصلی">
          {nav.map(n => (
            <a key={n.id} href={`#${n.id}`} className={current.id === n.id ? 'on' : ''} aria-current={current.id === n.id ? 'page' : undefined}>
              <n.icon size={19} /><span>{n.label}</span>
            </a>
          ))}
        </nav>
        <div className="me">
          <div className="avatar">{me.data.name.slice(0, 1)}</div>
          <div><strong>{me.data.name}</strong><small>{ROLE_LABEL[me.data.role]}</small></div>
          <button className="icon-btn sm" onClick={() => signOut()} title="خروج" aria-label="خروج"><LogOut size={16} /></button>
        </div>
      </aside>
      <div className="main">
        <header className="top">
          <div><h1>{current.label}</h1><p>{project.title ?? project.name}</p></div>
          <div className="top-actions"><span className="today">{longDate(todayIso())}</span><Notifications onOpenTask={openTask} /><ThemeButton /></div>
        </header>
        <main key={current.id} className="page">
          {current.id === 'overview' && <Overview projectId={project.id} onOpenTask={openTask} onGoTasks={() => go('tasks')} />}
          {current.id === 'tasks' && <Tasks projectId={project.id} me={me.data} focusTask={focusTask} onFocused={() => setFocusTask(null)} />}
          {current.id === 'library' && <Library projectId={project.id} me={me.data} />}
          {current.id === 'reports' && <Reports projectId={project.id} />}
          {current.id === 'access' && <Access projectId={project.id} me={me.data} />}
        </main>
      </div>
    </div>
  );
}

function SignIn({ message }: { message?: string }) {
  return (
    <div className="signin">
      <section>
        <div className="logo lg">ن</div>
        <h1>داشبورد پروژه نور</h1>
        <p>{message ?? 'برای دیدن داشبورد با حساب سازمانی خود وارد شوید.'}</p>
        {!message && <button className="btn primary wide" onClick={() => signIn()}>ورود به سامانه</button>}
      </section>
    </div>
  );
}
