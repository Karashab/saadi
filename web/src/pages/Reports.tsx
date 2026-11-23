import { useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { Download } from 'lucide-react';
import { useProjectData } from '../App';
import { api, download, type Summary } from '../api';
import { isoFromDay, jalaliText, pct, toFa } from '../model';
import { Progress, Skeleton, useToast } from '../ui';

export default function Reports({ projectId }: { projectId: string }) {
  const toast = useToast();
  const { tree, loading } = useProjectData(projectId);
  const [busy, setBusy] = useState(false);
  const summary = useQuery({ queryKey: ['summary', projectId], queryFn: () => api<Summary>(`/projects/${projectId}/reports/summary`) });
  if (loading || summary.isLoading) return <Skeleton rows={7} />;
  if (summary.error || !summary.data) return <div className="card">{(summary.error as Error)?.message ?? 'گزارش در دسترس نیست.'}</div>;
  const s = summary.data;
  const stats = [
    ['کل تسک‌ها', s.tasks], ['تکمیل شده', s.completed], ['در حال انجام', s.inProgress],
    ['شروع نشده', s.notStarted], ['عقب‌افتاده', s.overdue], ['بدون تاریخ', s.withoutDates],
  ] as const;
  const exportFile = async () => {
    setBusy(true);
    try { await download(`/projects/${projectId}/reports/tasks.xlsx`, 'noor-tasks.xlsx'); toast('فایل Excel آماده شد.'); }
    catch (e) { toast((e as Error).message, true); } finally { setBusy(false); }
  };
  return (
    <div className="stack">
      <div className="toolbar end"><button className="btn primary" onClick={exportFile} disabled={busy}><Download size={16} />{busy ? 'در حال آماده‌سازی…' : 'خروجی Excel'}</button></div>
      <section className="stat-strip card">{stats.map(([label, value]) => <div key={label}><small>{label}</small><strong>{toFa(value)}</strong></div>)}</section>
      <section className="card">
        <header className="card-head"><div><h2>گزارش فازها</h2><p>مقایسه پیشرفت واقعی با برنامه تا امروز</p></div></header>
        <div className="report-wrap">
          <table className="report">
            <thead><tr><th>فاز</th><th>شروع</th><th>پایان</th><th>تسک</th><th>تکمیل</th><th>عقب‌افتاده</th><th>برنامه</th><th>واقعی</th><th>انحراف</th></tr></thead>
            <tbody>
              {tree.roots.filter(p => p.roll.leaves > 0).map(p => {
                const gap = p.roll.actual - p.roll.planned;
                return (
                  <tr key={p.task.id}>
                    <td className="r-name">{p.task.name}</td>
                    <td className="num">{p.roll.start != null ? jalaliText(isoFromDay(p.roll.start)) : '—'}</td>
                    <td className="num">{p.roll.finish != null ? jalaliText(isoFromDay(p.roll.finish)) : '—'}</td>
                    <td className="num">{toFa(p.roll.leaves)}</td><td className="num">{toFa(p.roll.done)}</td><td className="num">{toFa(p.roll.late)}</td>
                    <td className="num">{pct(p.roll.planned, 1)}</td>
                    <td><span className="t-prog"><Progress value={p.roll.actual} planned={p.roll.planned} /><b>{pct(p.roll.actual, 1)}</b></span></td>
                    <td dir="ltr" style={{ textAlign: 'right' }} className={`num ${gap < -0.05 ? 'neg' : gap > 0.05 ? 'pos' : ''}`}>{gap > 0.05 ? '+' : gap < -0.05 ? '−' : ''}{pct(Math.abs(gap), 1)}</td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        </div>
      </section>
    </div>
  );
}
