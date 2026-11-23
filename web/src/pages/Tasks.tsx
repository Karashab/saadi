import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useVirtualizer } from '@tanstack/react-virtual';
import { ChevronLeft, ChevronsDownUp, ChevronsUpDown, FileText, Paperclip, Plus, Search, StickyNote, Trash2, Upload } from 'lucide-react';
import { useProjectData } from '../App';
import { ApiError, api, download, upload, type DocumentItem, type Me, type Organization, type Task } from '../api';
import { STATUS_LABEL, canCreate, canDeleteItems, canEdit, fileSize, isManager, isoFromDay, jalaliText, pct, statusOf, toFa, type Node, type Status } from '../model';
import { Drawer, Empty, JalaliInput, Modal, PercentInput, Popover, Progress, Skeleton, StatusBadge, useToast } from '../ui';

type Filter = 'all' | Status | 'late' | 'undated';
const FILTERS: { id: Filter; label: string }[] = [
  { id: 'all', label: 'همه' }, { id: 'late', label: 'عقب‌افتاده' }, { id: 'doing', label: 'در حال انجام' },
  { id: 'todo', label: 'شروع نشده' }, { id: 'done', label: 'تکمیل شده' }, { id: 'undated', label: 'بدون تاریخ' },
];

function useTaskPatch(projectId: string) {
  const client = useQueryClient();
  const toast = useToast();
  return useMutation({
    mutationFn: ({ task, patch }: { task: Task; patch: Record<string, unknown> }) => api<Task>(`/tasks/${task.id}`, 'PATCH', { version: task.version, ...patch }),
    onSuccess: saved => client.setQueryData<Task[]>(['tasks', projectId], list => list?.map(t => (t.id === saved.id ? saved : t))),
    onError: (e: Error) => {
      toast(e.message, true);
      if (e instanceof ApiError && e.status === 409) client.invalidateQueries({ queryKey: ['tasks', projectId] });
    },
  });
}

function StatusPicker({ task, projectId, anchor, onClose }: { task: Task; projectId: string; anchor: DOMRect; onClose: () => void }) {
  const patch = useTaskPatch(projectId);
  const toast = useToast();
  const [asking, setAsking] = useState(false);
  const [value, setValue] = useState('');
  const current = Number(task.percent_complete ?? 0);
  const set = (percentComplete: number) => { patch.mutateAsync({ task, patch: { percentComplete } }).then(() => toast('وضعیت به‌روز شد.')).catch(() => { /* reported by the mutation */ }); onClose(); };
  const save = () => { const n = Number(value); if (!(n >= 1 && n <= 99)) return toast('عددی بین ۱ تا ۹۹ وارد کنید.', true); set(n); };
  return (
    <Popover anchor={anchor} onClose={onClose}>
      <p className="pop-title">{task.name}</p>
      <div className="status-options">
        {(['todo', 'doing', 'done'] as Status[]).map(s => {
          const active = (s === 'done' && current >= 100) || (s === 'doing' && current > 0 && current < 100) || (s === 'todo' && current === 0);
          return <button key={s} className={active || (asking && s === 'doing') ? 'on' : ''} onClick={() => (s === 'todo' ? set(0) : s === 'done' ? set(100) : (setAsking(true), setValue(current > 0 && current < 100 ? String(current) : '')))}><StatusBadge status={s} /></button>;
        })}
      </div>
      {asking && (
        <div className="ask">
          <label>چند درصد انجام شده؟</label>
          <div className="quick">{[10, 25, 50, 75, 90].map(q => <button key={q} onClick={() => set(q)}>{pct(q)}</button>)}</div>
          <div className="row"><PercentInput value={value} onChange={setValue} autoFocus /><button className="btn primary" onClick={save}>ثبت</button></div>
        </div>
      )}
    </Popover>
  );
}

function TaskFiles({ task, projectId, canUpload }: { task: Task; projectId: string; canUpload: boolean }) {
  const client = useQueryClient();
  const toast = useToast();
  const [progress, setProgress] = useState<number | null>(null);
  const key = ['documents', projectId, 'task', task.id];
  const { data = [] } = useQuery({ queryKey: key, queryFn: () => api<DocumentItem[]>(`/projects/${projectId}/documents?taskId=${task.id}`) });
  const send = async (file: File) => {
    const form = new FormData();
    form.append('file', file); form.append('taskId', task.id);
    try {
      setProgress(0);
      await upload(`/projects/${projectId}/documents`, form, setProgress);
      toast('فایل بارگذاری شد.');
      client.invalidateQueries({ queryKey: ['documents', projectId] });
    } catch (e) { toast((e as Error).message, true); } finally { setProgress(null); }
  };
  return (
    <section className="drawer-section">
      <h3><Paperclip size={15} />فایل‌های پیوست</h3>
      {data.map(d => (
        <button key={d.id} className="file-row" onClick={() => download(`/document-versions/${d.latest.id}/download`, d.latest.fileName).catch(e => toast(e.message, true))}>
          <FileText size={17} /><span>{d.title}<small>{fileSize(d.latest.sizeBytes)} · نسخه {toFa(d.latest.versionNo)}</small></span>
        </button>
      ))}
      {canUpload && (
        <label className="drop small">
          <input type="file" hidden onChange={e => { const f = e.target.files?.[0]; if (f) send(f); e.target.value = ''; }} />
          <Upload size={16} />{progress == null ? 'افزودن فایل' : `در حال بارگذاری ${pct(progress * 100)}`}
        </label>
      )}
      {!canUpload && data.length === 0 && <p className="muted">فایلی پیوست نشده است.</p>}
    </section>
  );
}

function TaskDrawer({ node, projectId, me, onClose, onAddChild }: { node: Node; projectId: string; me: Me; onClose: () => void; onAddChild: () => void }) {
  const task = node.task;
  const leaf = node.children.length === 0;
  const editable = canEdit(me.role) && !task.headingOnly;
  const canRename = editable && me.role !== 'executor';
  const patch = useTaskPatch(projectId);
  const client = useQueryClient();
  const toast = useToast();
  const [form, setForm] = useState({
    name: task.name, startDate: task.start_date, finishDate: task.finish_date, durationDays: task.duration_days == null ? '' : String(task.duration_days),
    percent: String(Math.round(Number(task.percent_complete ?? 0))), assigneeText: task.assignee_text ?? '', priority: task.priority ?? 'normal', notes: task.notes ?? '',
  });
  const organizations = useQuery({ queryKey: ['organizations', projectId], queryFn: () => api<Organization[]>(`/projects/${projectId}/organizations`), enabled: isManager(me.role) });
  const [company, setCompany] = useState(task.organization_id ?? '');
  const assign = useMutation({
    mutationFn: () => api<{ changed: number }>(`/projects/${projectId}/tasks/assign-organization`, 'POST', { taskIds: [task.id], organizationId: company || null }),
    onSuccess: r => { toast(`${toFa(r.changed)} ردیف به‌روز شد.`); client.invalidateQueries({ queryKey: ['tasks', projectId] }); onClose(); },
    onError: (e: Error) => toast(e.message, true),
  });
  const remove = useMutation({
    mutationFn: () => api(`/tasks/${task.id}`, 'DELETE'),
    onSuccess: () => { toast('تسک حذف شد.'); client.invalidateQueries({ queryKey: ['tasks', projectId] }); onClose(); },
    onError: (e: Error) => toast(e.message, true),
  });
  const set = <K extends keyof typeof form>(key: K, value: (typeof form)[K]) => setForm(f => ({ ...f, [key]: value }));

  const save = () => {
    const change: Record<string, unknown> = {};
    if (form.name !== task.name) change.name = form.name;
    if (leaf) {
      if (form.startDate !== task.start_date) change.startDate = form.startDate;
      if (form.finishDate !== task.finish_date) change.finishDate = form.finishDate;
      if (form.durationDays !== (task.duration_days == null ? '' : String(task.duration_days))) change.durationDays = form.durationDays === '' ? null : Number(form.durationDays);
      const p = Number(form.percent);
      if (p > 100) return toast('درصد باید بین ۰ تا ۱۰۰ باشد.', true);
      if (p !== Math.round(Number(task.percent_complete ?? 0))) change.percentComplete = p;
    }
    if (form.assigneeText !== (task.assignee_text ?? '')) change.assigneeText = form.assigneeText || null;
    if (form.priority !== (task.priority ?? 'normal')) change.priority = form.priority;
    if (form.notes !== (task.notes ?? '')) change.notes = form.notes || null;
    if (!Object.keys(change).length) return onClose();
    patch.mutate({ task, patch: change }, { onSuccess: () => { toast('تغییرات ذخیره شد.'); onClose(); } });
  };

  return (
    <Drawer
      onClose={onClose}
      title={<><small>{task.wbs ? toFa(task.wbs) : 'تسک'}</small><StatusBadge status={statusOf(node.roll)} late={node.roll.isLate} /></>}
      footer={editable ? (
        <>
          <button className="btn primary" onClick={save} disabled={patch.isPending}>{patch.isPending ? 'در حال ذخیره…' : 'ذخیره تغییرات'}</button>
          {canCreate(me.role) && <button className="btn" onClick={onAddChild}><Plus size={15} />زیرتسک</button>}
          {canDeleteItems(me.role) && leaf && <button className="icon-btn danger" title="حذف تسک" aria-label="حذف تسک" onClick={() => confirm('این تسک حذف شود؟') && remove.mutate()}><Trash2 size={17} /></button>}
        </>
      ) : undefined}
    >
      {task.headingOnly ? <h2 className="drawer-name">{task.name}</h2> : (
        <>
          <div className="field"><label>عنوان</label><textarea className="input title-input" rows={2} value={form.name} disabled={!canRename} onChange={e => set('name', e.target.value)} /></div>
          {leaf ? (
            <>
              <div className="field">
                <label>پیشرفت <b>{pct(Number(form.percent) || 0)}</b></label>
                <input type="range" min={0} max={100} step={5} value={Number(form.percent) || 0} disabled={!editable} onChange={e => set('percent', e.target.value)} aria-label="درصد پیشرفت" />
              </div>
              <div className="fields">
                <JalaliInput label="تاریخ شروع" value={form.startDate} disabled={!editable} onChange={v => set('startDate', v)} />
                <JalaliInput label="تاریخ پایان" value={form.finishDate} disabled={!editable} onChange={v => set('finishDate', v)} />
              </div>
              <div className="fields">
                <div className="field"><label>مدت (روز)</label><input className="input num" inputMode="numeric" value={toFa(form.durationDays)} disabled={!editable} onChange={e => set('durationDays', e.target.value.replace(/[۰-۹]/g, d => String('۰۱۲۳۴۵۶۷۸۹'.indexOf(d))).replace(/[^\d.]/g, ''))} /></div>
                <div className="field"><label>اولویت</label><select className="input" value={form.priority} disabled={!editable} onChange={e => set('priority', e.target.value)}><option value="high">بالا</option><option value="normal">معمولی</option><option value="low">پایین</option></select></div>
              </div>
            </>
          ) : (
            <div className="rollup">
              <div><small>پیشرفت</small><b>{pct(node.roll.actual, 1)}</b></div>
              <div><small>شروع</small><b>{node.roll.start != null ? jalaliText(isoFromDay(node.roll.start)) : '—'}</b></div>
              <div><small>پایان</small><b>{node.roll.finish != null ? jalaliText(isoFromDay(node.roll.finish)) : '—'}</b></div>
              <div><small>تسک</small><b>{toFa(node.roll.leaves)}</b></div>
            </div>
          )}
          <div className="field"><label>مسئول</label><input className="input" value={form.assigneeText} disabled={!editable} onChange={e => set('assigneeText', e.target.value)} /></div>
          <div className="field"><label>یادداشت</label><textarea className="input" rows={3} placeholder="یادداشت خود را بنویسید…" value={form.notes} disabled={!editable} onChange={e => set('notes', e.target.value)} /></div>
          {isManager(me.role) && (
            <section className="drawer-section">
              <h3>شرکت مسئول</h3>
              <div className="row">
                <select className="input" value={company} onChange={e => setCompany(e.target.value)}>
                  <option value="">فقط مدیرها</option>
                  {organizations.data?.map(o => <option key={o.id} value={o.id}>{o.name}</option>)}
                </select>
                <button className="btn" disabled={assign.isPending || company === (task.organization_id ?? '')} onClick={() => assign.mutate()}>{leaf ? 'اعمال' : 'اعمال به کل شاخه'}</button>
              </div>
            </section>
          )}
          {leaf && <TaskFiles task={task} projectId={projectId} canUpload={editable} />}
          {task.source_info && <section className="drawer-section"><h3>اطلاعات منبع</h3><div className="source">{task.source_info.split(' · ').map(x => <span key={x}>{x}</span>)}</div></section>}
        </>
      )}
    </Drawer>
  );
}

function NewTask({ projectId, parent, onClose }: { projectId: string; parent: Node | null; onClose: () => void }) {
  const client = useQueryClient();
  const toast = useToast();
  const [name, setName] = useState('');
  const [startDate, setStart] = useState<string | null>(null);
  const [finishDate, setFinish] = useState<string | null>(null);
  const create = useMutation({
    mutationFn: () => api(`/projects/${projectId}/tasks`, 'POST', { name, parentId: parent?.task.id ?? null, startDate, finishDate, organizationId: parent?.task.organization_id ?? null }),
    onSuccess: () => { toast('تسک اضافه شد.'); client.invalidateQueries({ queryKey: ['tasks', projectId] }); onClose(); },
    onError: (e: Error) => toast(e.message, true),
  });
  return (
    <Modal title={parent ? `زیرتسک برای «${parent.task.name}»` : 'تسک جدید'} onClose={onClose}>
      <form onSubmit={e => { e.preventDefault(); if (name.trim()) create.mutate(); }}>
        <div className="field"><label>عنوان</label><input className="input" autoFocus value={name} onChange={e => setName(e.target.value)} /></div>
        <div className="fields"><JalaliInput label="تاریخ شروع" value={startDate} onChange={setStart} /><JalaliInput label="تاریخ پایان" value={finishDate} onChange={setFinish} /></div>
        <div className="modal-actions"><button className="btn primary" disabled={!name.trim() || create.isPending}>افزودن تسک</button><button type="button" className="btn" onClick={onClose}>انصراف</button></div>
      </form>
    </Modal>
  );
}

export default function Tasks({ projectId, me, focusTask, onFocused }: { projectId: string; me: Me; focusTask: string | null; onFocused: () => void }) {
  const { tree, loading } = useProjectData(projectId);
  const [query, setQuery] = useState('');
  const [filter, setFilter] = useState<Filter>('all');
  const [openNormal, setOpenNormal] = useState<Record<string, boolean>>({});
  const [closedFiltered, setClosedFiltered] = useState<Record<string, boolean>>({});
  const [selected, setSelected] = useState<string | null>(null);
  const [picker, setPicker] = useState<{ id: string; rect: DOMRect } | null>(null);
  const [adding, setAdding] = useState<{ parent: Node | null } | null>(null);
  const scroller = useRef<HTMLDivElement>(null);

  const q = query.trim().toLowerCase();
  const filtering = !!q || filter !== 'all';
  useEffect(() => setClosedFiltered({}), [q, filter]);

  const matches = useCallback((n: Node) => {
    const leaf = n.children.length === 0;
    if (q && ![n.task.name, n.task.wbs, n.task.assignee_text, n.task.notes, n.task.source_info].some(x => x?.toLowerCase().includes(q))) return false;
    if (filter === 'all') return true;
    if (!leaf || n.task.headingOnly) return false;
    if (filter === 'late') return n.roll.isLate;
    if (filter === 'undated') return n.roll.finish == null;
    return statusOf(n.roll) === filter;
  }, [q, filter]);

  const rows = useMemo(() => {
    const seen = new Map<string, boolean>();
    const visible = (n: Node): boolean => {
      const known = seen.get(n.task.id);
      if (known !== undefined) return known;
      const v = n.children.map(visible).some(Boolean) || matches(n);
      seen.set(n.task.id, v);
      return v;
    };
    const out: Node[] = [];
    const walk = (n: Node) => {
      if (filtering && !visible(n)) return;
      out.push(n);
      const open = filtering ? !closedFiltered[n.task.id] : openNormal[n.task.id] ?? n.depth < 1;
      if (open) n.children.forEach(walk);
    };
    tree.roots.forEach(walk);
    return out;
  }, [tree, filtering, matches, closedFiltered, openNormal]);

  const virtual = useVirtualizer({ count: rows.length, getScrollElement: () => scroller.current, estimateSize: () => 54, overscan: 12 });

  useEffect(() => {
    if (!focusTask || !tree.nodes.has(focusTask)) return;
    const next: Record<string, boolean> = {};
    for (let p = tree.nodes.get(focusTask)!.parent; p; p = p.parent) next[p.task.id] = true;
    setQuery(''); setFilter('all'); setOpenNormal(o => ({ ...o, ...next })); setSelected(focusTask); onFocused();
  }, [focusTask, tree, onFocused]);

  const toggle = (n: Node) => {
    if (filtering) setClosedFiltered(c => ({ ...c, [n.task.id]: !c[n.task.id] }));
    else setOpenNormal(o => ({ ...o, [n.task.id]: !(o[n.task.id] ?? n.depth < 1) }));
  };
  const setAll = (open: boolean) => {
    const value: Record<string, boolean> = {};
    tree.nodes.forEach(n => { if (n.children.length) value[n.task.id] = filtering ? !open : open; });
    if (filtering) setClosedFiltered(value); else setOpenNormal(value);
  };

  if (loading) return <Skeleton rows={9} />;
  const selectedNode = selected ? tree.nodes.get(selected) : undefined;
  const pickerNode = picker ? tree.nodes.get(picker.id) : undefined;

  return (
    <div className="tasks-page">
      <div className="toolbar">
        <div className="search"><Search size={16} /><input value={query} onChange={e => setQuery(e.target.value)} placeholder="جستجو در عنوان، WBS، مسئول…" aria-label="جستجو" /></div>
        <div className="chips" role="group" aria-label="فیلتر وضعیت">
          {FILTERS.map(f => <button key={f.id} className={filter === f.id ? 'on' : ''} aria-pressed={filter === f.id} onClick={() => setFilter(f.id)}>{f.label}</button>)}
        </div>
        <div className="tool-end">
          <button className="icon-btn" title="باز کردن همه" aria-label="باز کردن همه" onClick={() => setAll(true)}><ChevronsUpDown size={18} /></button>
          <button className="icon-btn" title="بستن همه" aria-label="بستن همه" onClick={() => setAll(false)}><ChevronsDownUp size={18} /></button>
          {canCreate(me.role) && <button className="btn primary" onClick={() => setAdding({ parent: null })}><Plus size={16} />تسک جدید</button>}
        </div>
      </div>

      <section className="table-card">
        <div className="t-row t-head"><span>فعالیت</span><span>شروع</span><span>پایان</span><span>پیشرفت</span><span>وضعیت</span></div>
        <div className="t-scroll" ref={scroller}>
          {rows.length === 0 ? <Empty icon={<Search size={20} />} title="موردی پیدا نشد" text="فیلترها را تغییر دهید." /> : (
            <div style={{ height: virtual.getTotalSize(), position: 'relative' }}>
              {virtual.getVirtualItems().map(item => {
                const n = rows[item.index];
                const leaf = n.children.length === 0;
                const open = filtering ? !closedFiltered[n.task.id] : openNormal[n.task.id] ?? n.depth < 1;
                const status = statusOf(n.roll);
                return (
                  <div key={n.task.id} className={`t-row ${n.depth === 0 ? 'phase-row' : leaf ? '' : 'parent-row'} ${selected === n.task.id ? 'selected' : ''}`}
                    style={{ transform: `translateY(${item.start}px)`, height: item.size }} onClick={() => setSelected(n.task.id)}>
                    <div className="t-name" style={{ paddingInlineStart: Math.min(n.depth, 7) * 18 }}>
                      {leaf ? <span className="tg-space" /> : <button className={`tg ${open ? 'open' : ''}`} aria-expanded={open} aria-label={open ? 'بستن' : 'باز کردن'} onClick={e => { e.stopPropagation(); toggle(n); }}><ChevronLeft size={16} /></button>}
                      <small>{n.task.wbs ? toFa(n.task.wbs) : ''}</small>
                      <span className={`ttl ${leaf && status === 'done' ? 'done' : ''}`} title={n.task.name}>{n.task.name}</span>
                      {n.task.notes && <span className="note-chip" title={n.task.notes}><StickyNote size={12} />یادداشت</span>}
                      {n.task.assignee_text && <span className="who">{n.task.assignee_text}</span>}
                    </div>
                    <span className="num t-date">{n.roll.start != null ? jalaliText(isoFromDay(n.roll.start)) : '—'}</span>
                    <span className={`num t-date ${n.roll.isLate ? 'late' : ''}`}>{n.roll.finish != null ? jalaliText(isoFromDay(n.roll.finish)) : '—'}</span>
                    <span className="t-prog"><Progress value={n.roll.actual} tone={n.roll.actual >= 99.95 ? 'good' : undefined} /><b>{pct(n.roll.actual, leaf ? 0 : 1)}</b></span>
                    <span>{leaf && canEdit(me.role) && !n.task.headingOnly
                      ? <button className="status-btn" title="تغییر وضعیت" onClick={e => { e.stopPropagation(); setPicker({ id: n.task.id, rect: e.currentTarget.getBoundingClientRect() }); }}><StatusBadge status={status} late={n.roll.isLate} /></button>
                      : !n.task.headingOnly && <StatusBadge status={status} late={n.roll.isLate} />}</span>
                  </div>
                );
              })}
            </div>
          )}
        </div>
        <footer className="t-foot">{toFa(rows.length)} ردیف نمایش داده می‌شود{filter !== 'all' && ` · فیلتر: ${filter === 'late' ? STATUS_LABEL.late : FILTERS.find(f => f.id === filter)?.label}`}</footer>
      </section>

      {picker && pickerNode && <StatusPicker task={pickerNode.task} projectId={projectId} anchor={picker.rect} onClose={() => setPicker(null)} />}
      {selectedNode && <TaskDrawer key={`${selectedNode.task.id}:${selectedNode.task.version}`} node={selectedNode} projectId={projectId} me={me} onClose={() => setSelected(null)} onAddChild={() => { setAdding({ parent: selectedNode }); setSelected(null); }} />}
      {adding && <NewTask projectId={projectId} parent={adding.parent} onClose={() => setAdding(null)} />}
    </div>
  );
}
