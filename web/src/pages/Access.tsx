import { useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Building2, Plus, ShieldCheck, Trash2, Users } from 'lucide-react';
import { api, type DirectoryUser, type Me, type Member, type Organization, type Role } from '../api';
import { ROLE_LABEL, isManager, toFa } from '../model';
import { Empty, Skeleton, useToast } from '../ui';

const ROLES: Role[] = ['system_admin', 'project_manager', 'company_admin', 'executor', 'viewer'];

export default function Access({ projectId, me }: { projectId: string; me: Me }) {
  const client = useQueryClient();
  const toast = useToast();
  const [name, setName] = useState('');
  const [search, setSearch] = useState('');
  const directory = useQuery({ queryKey: ['directory', projectId, search], queryFn: () => api<DirectoryUser[]>(`/projects/${projectId}/user-directory?q=${encodeURIComponent(search)}`), enabled: search.trim().length >= 2 });
  const organizations = useQuery({ queryKey: ['organizations', projectId], queryFn: () => api<Organization[]>(`/projects/${projectId}/organizations`) });
  const members = useQuery({ queryKey: ['members', projectId], queryFn: () => api<Member[]>(`/projects/${projectId}/members`) });
  const fail = (e: Error) => { toast(e.message, true); client.invalidateQueries({ queryKey: ['members', projectId] }); };
  const addCompany = useMutation({
    mutationFn: () => api(`/projects/${projectId}/organizations`, 'POST', { name }),
    onSuccess: () => { toast('شرکت اضافه شد.'); setName(''); client.invalidateQueries({ queryKey: ['organizations', projectId] }); },
    onError: fail,
  });
  const update = useMutation({
    mutationFn: (m: { userId: string; role: Role; organizationId: string | null }) => api(`/projects/${projectId}/members/${m.userId}`, 'PUT', { role: m.role, organizationId: m.organizationId }),
    onSuccess: () => { toast('دسترسی به‌روز شد.'); client.invalidateQueries({ queryKey: ['members', projectId] }); },
    onError: fail,
  });
  const remove = useMutation({
    mutationFn: (id: string) => api(`/projects/${projectId}/members/${id}`, 'DELETE'),
    onSuccess: () => { toast('دسترسی کاربر قطع شد.'); client.invalidateQueries({ queryKey: ['members', projectId] }); },
    onError: fail,
  });
  if (organizations.isLoading || members.isLoading) return <Skeleton rows={6} />;
  const roles = me.role === 'system_admin' ? ROLES : ROLES.filter(r => r !== 'system_admin');

  const change = (m: Member, role: Role, organizationId: string | null) => {
    const company = isManager(role) ? organizationId : organizationId ?? organizations.data?.[0]?.id ?? null;
    if (!isManager(role) && !company) return toast('برای این نقش اول یک شرکت تعریف کنید.', true);
    update.mutate({ userId: m.userId, role, organizationId: company });
  };

  return (
    <div className="grid-access">
      <section className="card">
        <header className="card-head"><div><h2>شرکت‌ها</h2><p>تسک‌ها را از صفحه تسک‌ها به شرکت وصل کنید</p></div></header>
        <form className="row" onSubmit={e => { e.preventDefault(); if (name.trim()) addCompany.mutate(); }}>
          <input className="input" value={name} onChange={e => setName(e.target.value)} placeholder="نام شرکت" aria-label="نام شرکت" />
          <button className="btn primary" disabled={!name.trim() || addCompany.isPending}><Plus size={16} />افزودن</button>
        </form>
        <div className="company-list">
          {organizations.data?.length === 0 && <Empty icon={<Building2 size={22} />} title="هنوز شرکتی تعریف نشده" text="تا وقتی شرکت مشخص نشده، تسک‌ها فقط برای مدیرها باز است." />}
          {organizations.data?.map(o => (
            <div key={o.id}><Building2 size={17} /><span>{o.name}</span><small>{toFa(members.data?.filter(m => m.organizationId === o.id).length ?? 0)} کاربر</small></div>
          ))}
        </div>
      </section>
      <section className="card">
        <header className="card-head"><div><h2>کاربران و نقش‌ها</h2><p>{toFa(members.data?.length ?? 0)} کاربر در این پروژه</p></div><ShieldCheck size={19} className="muted" /></header>
        <div className="add-member">
          <div className="search"><Plus size={16} /><input value={search} onChange={e => setSearch(e.target.value)} placeholder="افزودن کاربر: نام، نام کاربری یا ایمیل…" aria-label="جستجوی کاربر" /></div>
          {search.trim().length >= 2 && (
            <div className="directory">
              {directory.data?.filter(u => !members.data?.some(m => m.userId === u.id)).map(u => (
                <button key={u.id} onClick={() => { change({ userId: u.id, name: u.name, role: 'viewer', organizationId: null }, 'viewer', null); setSearch(''); }}>
                  <span className="avatar">{u.name.slice(0, 1)}</span><span><strong>{u.name}</strong><small>{u.username ?? u.email ?? ''}</small></span><em>افزودن به‌عنوان ناظر</em>
                </button>
              ))}
              {directory.data && directory.data.filter(u => !members.data?.some(m => m.userId === u.id)).length === 0 && <p className="muted">کاربری پیدا نشد. هر کاربر بعد از اولین ورود به سامانه اینجا دیده می‌شود.</p>}
            </div>
          )}
        </div>
        {members.data?.length === 0 ? <Empty icon={<Users size={22} />} title="کاربری نیست" /> : (
          <div className="members">
            {members.data?.map(m => {
              const locked = m.role === 'system_admin' && me.role !== 'system_admin';
              return (
                <div key={m.userId} className="member">
                  <div className="avatar">{m.name.slice(0, 1)}</div>
                  <strong>{m.name}{m.userId === me.userId && <small> (شما)</small>}</strong>
                  <select className="input" value={m.role} disabled={locked} aria-label="نقش" onChange={e => change(m, e.target.value as Role, m.organizationId)}>
                    {(locked ? ROLES : roles).map(r => <option key={r} value={r}>{ROLE_LABEL[r]}</option>)}
                  </select>
                  <select className="input" value={m.organizationId ?? ''} disabled={locked} aria-label="شرکت" onChange={e => change(m, m.role, e.target.value || null)}>
                    {isManager(m.role) && <option value="">همه شرکت‌ها</option>}
                    {organizations.data?.map(o => <option key={o.id} value={o.id}>{o.name}</option>)}
                  </select>
                  <button className="icon-btn danger" disabled={locked} title="قطع دسترسی" aria-label="قطع دسترسی" onClick={() => confirm(`دسترسی ${m.name} قطع شود؟`) && remove.mutate(m.userId)}><Trash2 size={16} /></button>
                </div>
              );
            })}
          </div>
        )}
      </section>
    </div>
  );
}
