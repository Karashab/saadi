import { useRef, useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Download, FileArchive, FileImage, FileSpreadsheet, FileText, FolderOpen, FolderPlus, History, Library as LibraryIcon, Search, Trash2, Upload } from 'lucide-react';
import { api, download, upload, type Category, type DocumentItem, type DocumentVersion, type Me } from '../api';
import { canDeleteItems, canEdit, fileSize, isManager, jalaliText, pct, toFa } from '../model';
import { Empty, Modal, Skeleton, useToast } from '../ui';

const iconFor = (name: string) => {
  const ext = name.split('.').pop()?.toLowerCase() ?? '';
  if (['xls', 'xlsx', 'csv'].includes(ext)) return <FileSpreadsheet size={20} />;
  if (['jpg', 'jpeg', 'png', 'tif', 'tiff'].includes(ext)) return <FileImage size={20} />;
  if (['zip', 'rar', '7z'].includes(ext)) return <FileArchive size={20} />;
  return <FileText size={20} />;
};

function Versions({ doc, canUpload, onClose, onChanged }: { doc: DocumentItem; canUpload: boolean; onClose: () => void; onChanged: () => void }) {
  const toast = useToast();
  const [progress, setProgress] = useState<number | null>(null);
  const versions = useQuery({ queryKey: ['versions', doc.id], queryFn: () => api<DocumentVersion[]>(`/documents/${doc.id}/versions`) });
  const send = async (file: File) => {
    const form = new FormData();
    form.append('file', file);
    try {
      setProgress(0);
      await upload(`/documents/${doc.id}/versions`, form, setProgress);
      toast('نسخه جدید ثبت شد.');
      versions.refetch();
      onChanged();
    } catch (e) { toast((e as Error).message, true); } finally { setProgress(null); }
  };
  return (
    <Modal title={doc.title} onClose={onClose}>
      <div className="versions">
        {versions.isLoading && <Skeleton rows={2} />}
        {versions.data?.map((v, i) => (
          <div key={v.id} className="version">
            <span className={`vno ${i === 0 ? 'latest' : ''}`}>{toFa(v.versionNo)}</span>
            <div><strong>{v.fileName}</strong><small>{v.uploadedBy} · {jalaliText(v.uploadedAt)} · {fileSize(v.sizeBytes)}</small></div>
            <button className="icon-btn" title="دانلود" aria-label="دانلود" onClick={() => download(`/document-versions/${v.id}/download`, v.fileName).catch(e => toast(e.message, true))}><Download size={17} /></button>
          </div>
        ))}
      </div>
      {canUpload && (
        <label className="drop small">
          <input type="file" hidden onChange={e => { const f = e.target.files?.[0]; if (f) send(f); e.target.value = ''; }} />
          <Upload size={16} />{progress == null ? 'بارگذاری نسخه جدید' : `در حال بارگذاری ${pct(progress * 100)}`}
        </label>
      )}
    </Modal>
  );
}

export default function Library({ projectId, me }: { projectId: string; me: Me }) {
  const client = useQueryClient();
  const toast = useToast();
  const [categoryId, setCategoryId] = useState<string | null>(null);
  const [query, setQuery] = useState('');
  const [dragging, setDragging] = useState(false);
  const [uploads, setUploads] = useState<{ name: string; progress: number }[]>([]);
  const [opened, setOpened] = useState<DocumentItem | null>(null);
  const [newCategory, setNewCategory] = useState<string | null>(null);
  const input = useRef<HTMLInputElement>(null);
  const canUpload = canEdit(me.role);

  const categories = useQuery({ queryKey: ['categories', projectId], queryFn: () => api<Category[]>(`/projects/${projectId}/document-categories`) });
  const all = useQuery({ queryKey: ['documents', projectId, 'all'], queryFn: () => api<DocumentItem[]>(`/projects/${projectId}/documents`) });
  const refresh = () => client.invalidateQueries({ queryKey: ['documents', projectId] });
  const remove = useMutation({ mutationFn: (id: string) => api(`/documents/${id}`, 'DELETE'), onSuccess: () => { toast('مدرک حذف شد.'); refresh(); }, onError: (e: Error) => toast(e.message, true) });
  const addCategory = useMutation({
    mutationFn: (name: string) => api(`/projects/${projectId}/document-categories`, 'POST', { name }),
    onSuccess: () => { toast('دسته اضافه شد.'); setNewCategory(null); client.invalidateQueries({ queryKey: ['categories', projectId] }); },
    onError: (e: Error) => toast(e.message, true),
  });

  const sendFiles = async (files: File[]) => {
    if (!canUpload) return;
    for (const file of files) {
      setUploads(u => [...u, { name: file.name, progress: 0 }]);
      const form = new FormData();
      form.append('file', file);
      if (categoryId) form.append('categoryId', categoryId);
      try {
        await upload(`/projects/${projectId}/documents`, form, p => setUploads(u => u.map(x => (x.name === file.name ? { ...x, progress: p } : x))));
        toast(`«${file.name}» بارگذاری شد.`);
      } catch (e) { toast(`${file.name}: ${(e as Error).message}`, true); }
      setUploads(u => u.filter(x => x.name !== file.name));
    }
    refresh();
  };

  const q = query.trim().toLowerCase();
  const docs = (all.data ?? []).filter(d => (!categoryId || d.categoryId === categoryId)
    && (!q || [d.title, d.code, d.latest.fileName].some(x => x?.toLowerCase().includes(q))));
  const count = (id: string) => (all.data ?? []).filter(d => d.categoryId === id).length;
  const current = categories.data?.find(c => c.id === categoryId);

  return (
    <div className="library">
      <aside className="cats card">
        <button className={!categoryId ? 'on' : ''} onClick={() => setCategoryId(null)}><LibraryIcon size={17} /><span>همه مدارک</span><b>{toFa(all.data?.length ?? 0)}</b></button>
        {categories.data?.map(c => <button key={c.id} className={categoryId === c.id ? 'on' : ''} onClick={() => setCategoryId(c.id)}><FolderOpen size={17} /><span>{c.name}</span><b>{toFa(count(c.id))}</b></button>)}
        {isManager(me.role) && (newCategory == null
          ? <button className="add-cat" onClick={() => setNewCategory('')}><FolderPlus size={17} /><span>دسته جدید</span></button>
          : <form onSubmit={e => { e.preventDefault(); if (newCategory.trim()) addCategory.mutate(newCategory); }}><input className="input" autoFocus value={newCategory} placeholder="نام دسته" onChange={e => setNewCategory(e.target.value)} onBlur={() => !newCategory && setNewCategory(null)} /></form>)}
      </aside>

      <section className={`lib-main card ${dragging ? 'dragging' : ''}`}
        onDragOver={e => { if (canUpload) { e.preventDefault(); setDragging(true); } }}
        onDragLeave={e => { if (!e.currentTarget.contains(e.relatedTarget as globalThis.Node)) setDragging(false); }}
        onDrop={e => { e.preventDefault(); setDragging(false); sendFiles([...e.dataTransfer.files]); }}>
        <div className="toolbar">
          <div className="search"><Search size={16} /><input value={query} onChange={e => setQuery(e.target.value)} placeholder="جستجو در عنوان، کد و نام فایل…" aria-label="جستجوی مدارک" /></div>
          {canUpload && <button className="btn primary" onClick={() => input.current?.click()}><Upload size={16} />بارگذاری{current ? ` در ${current.name}` : ''}</button>}
          <input ref={input} type="file" multiple hidden onChange={e => { sendFiles([...(e.target.files ?? [])]); e.target.value = ''; }} />
        </div>
        {uploads.map(u => <div className="upload-row" key={u.name}><span>{u.name}</span><i><b style={{ width: `${u.progress * 100}%` }} /></i><small>{pct(u.progress * 100)}</small></div>)}
        {all.isLoading ? <Skeleton rows={5} /> : docs.length === 0 ? (
          <Empty icon={<FolderOpen size={24} />} title={q ? 'مدرکی پیدا نشد' : 'هنوز مدرکی اینجا نیست'} text={canUpload && !q ? 'فایل‌ها را اینجا بکشید و رها کنید.' : undefined} />
        ) : (
          <div className="docs">
            {docs.map(d => (
              <article key={d.id} className="doc" onClick={() => setOpened(d)}>
                <span className="doc-icon">{iconFor(d.latest.fileName)}</span>
                <div><strong>{d.title}</strong><small>{d.code ? `${d.code} · ` : ''}{d.latest.fileName}</small></div>
                <span className="doc-meta"><b>نسخه {toFa(d.latest.versionNo)}</b><small>{fileSize(d.latest.sizeBytes)} · {jalaliText(d.latest.uploadedAt)}</small></span>
                <div className="doc-actions" onClick={e => e.stopPropagation()}>
                  <button className="icon-btn" title="نسخه‌ها" aria-label="نسخه‌ها" onClick={() => setOpened(d)}><History size={17} /></button>
                  <button className="icon-btn" title="دانلود" aria-label="دانلود" onClick={() => download(`/document-versions/${d.latest.id}/download`, d.latest.fileName).catch(e => toast(e.message, true))}><Download size={17} /></button>
                  {canDeleteItems(me.role) && <button className="icon-btn danger" title="حذف" aria-label="حذف" onClick={() => confirm('این مدرک حذف شود؟') && remove.mutate(d.id)}><Trash2 size={17} /></button>}
                </div>
              </article>
            ))}
          </div>
        )}
        {dragging && <div className="drop-hint"><Upload size={28} />فایل‌ها را رها کنید</div>}
      </section>
      {opened && <Versions doc={opened} canUpload={canUpload} onClose={() => setOpened(null)} onChanged={refresh} />}
    </div>
  );
}
