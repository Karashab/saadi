import { useMemo, useState } from 'react';
import { ArrowLeft, CircleAlert, CircleCheck, Gauge, Hourglass } from 'lucide-react';
import { useProjectData } from '../App';
import { MONTHS, dayNumber, isoFromDay, isoToJalali, jalaliText, jalaliToIso, pct, toFa, type Node } from '../model';
import { Empty, Progress, Skeleton } from '../ui';

function Kpi({ icon, label, value, note, tone }: { icon: React.ReactNode; label: string; value: string; note: string; tone?: string }) {
  return <article className="kpi"><span className={`kpi-icon ${tone ?? ''}`}>{icon}</span><p>{label}</p><strong>{value}</strong><small>{note}</small></article>;
}

function Timeline({ phases, today }: { phases: Node[]; today: number }) {
  const dated = phases.filter(p => p.roll.start != null && p.roll.finish != null);
  const [hover, setHover] = useState<string | null>(null);
  if (!dated.length) return <Empty icon={<Hourglass size={20} />} title="تاریخی برای نمایش نیست" />;
  const min = Math.min(...dated.map(p => p.roll.start!));
  const max = Math.max(...dated.map(p => p.roll.finish!));
  const span = Math.max(1, max - min + 1);
  const at = (day: number) => ((day - min) / span) * 100;
  const ticks: { left: number; label: string }[] = [];
  const first = isoToJalali(isoFromDay(min));
  for (let i = 0, y = first.jy, m = first.jm; i < 40; i++) {
    const day = dayNumber(jalaliToIso(y, m, 1));
    if (day > max) break;
    if (day >= min && (i % 3 === 0)) ticks.push({ left: at(day), label: `${MONTHS[m - 1]} ${toFa(String(y).slice(2))}` });
    m++; if (m > 12) { m = 1; y++; }
  }
  return (
    <div className="gantt">
      <div className="gantt-axis"><span />
        <div>{ticks.map(t => <small key={t.label} style={{ insetInlineStart: `${t.left}%` }}>{t.label}</small>)}</div>
      </div>
      {dated.map(p => {
        const s = at(p.roll.start!), w = ((p.roll.finish! - p.roll.start! + 1) / span) * 100;
        return (
          <div className="gantt-row" key={p.task.id} onMouseEnter={() => setHover(p.task.id)} onMouseLeave={() => setHover(null)}>
            <span title={p.task.name}>{p.task.name}</span>
            <div className="gantt-track">
              {ticks.map(t => <i key={t.label} style={{ insetInlineStart: `${t.left}%` }} />)}
              <div className="gantt-bar" style={{ insetInlineStart: `${s}%`, width: `${w}%` }}><b style={{ width: `${p.roll.actual}%` }} /></div>
              {today >= min && today <= max && <em style={{ insetInlineStart: `${at(today)}%` }} />}
              {hover === p.task.id && (
                <div className="tip" style={{ insetInlineStart: `${Math.min(68, s)}%` }}>
                  <strong>{p.task.name}</strong>
                  <span>{jalaliText(isoFromDay(p.roll.start!))} تا {jalaliText(isoFromDay(p.roll.finish!))}</span>
                  <span>واقعی {pct(p.roll.actual, 1)} · برنامه {pct(p.roll.planned, 1)}</span>
                </div>
              )}
            </div>
          </div>
        );
      })}
      <div className="legend"><span><i className="l-plan" />بازه برنامه</span><span><i className="l-actual" />پیشرفت واقعی</span><span><i className="l-today" />امروز</span></div>
    </div>
  );
}

export default function Overview({ projectId, onOpenTask, onGoTasks }: { projectId: string; onOpenTask: (id: string) => void; onGoTasks: () => void }) {
  const { tree, loading, error } = useProjectData(projectId);
  const late = useMemo(() => {
    const list: Node[] = [];
    const walk = (n: Node) => { if (!n.children.length && n.roll.isLate) list.push(n); n.children.forEach(walk); };
    tree.roots.forEach(walk);
    return list.sort((a, b) => a.roll.finish! - b.roll.finish!);
  }, [tree]);

  if (loading) return <Skeleton rows={7} />;
  if (error) return <Empty icon={<CircleAlert size={22} />} title="داده‌ها بارگذاری نشد" text={(error as Error).message} />;
  const t = tree.total;
  const gap = t.actual - t.planned;
  const remaining = t.finish != null ? Math.max(0, t.finish - tree.today) : null;
  const notStarted = Math.max(0, t.leaves - t.done - t.doing);
  const segments = [
    { key: 'done', label: 'تکمیل شده', value: t.done },
    { key: 'doing', label: 'در حال انجام', value: t.doing },
    { key: 'todo', label: 'شروع نشده', value: notStarted },
  ];

  return (
    <div className="stack">
      <section className="hero">
        <div>
          <p>پیشرفت واقعی پروژه</p>
          <strong>{pct(t.actual, 1)}</strong>
          <span className={gap < 0 ? 'behind' : 'ahead'}>{gap < 0 ? `${pct(-gap, 1)} عقب‌تر از برنامه` : `${pct(gap, 1)} جلوتر از برنامه`}</span>
        </div>
        <div className="hero-bars">
          <label>واقعی <b>{pct(t.actual, 1)}</b></label><div className="hero-meter"><i style={{ width: `${t.actual}%` }} /></div>
          <label>برنامه تا امروز <b>{pct(t.planned, 1)}</b></label><div className="hero-meter plan"><i style={{ width: `${t.planned}%` }} /></div>
        </div>
      </section>

      <section className="kpis">
        <Kpi icon={<CircleCheck size={19} />} tone="good" label="تکمیل شده" value={toFa(t.done)} note={`از ${toFa(t.leaves)} تسک`} />
        <Kpi icon={<Gauge size={19} />} tone="info" label="در حال انجام" value={toFa(t.doing)} note="تسک فعال" />
        <Kpi icon={<CircleAlert size={19} />} tone="bad" label="عقب‌افتاده" value={toFa(t.late)} note="مهلت گذشته و ناتمام" />
        <Kpi icon={<Hourglass size={19} />} label="زمان باقی‌مانده" value={remaining == null ? '—' : toFa(remaining)} note={remaining == null ? 'بدون تاریخ پایان' : 'روز تا پایان برنامه'} />
      </section>

      <div className="grid-2">
        <section className="card">
          <header className="card-head"><div><h2>پیشرفت فازها</h2><p>نوار: واقعی · نشانگر: برنامه تا امروز</p></div></header>
          <div className="phases">
            {tree.roots.filter(p => p.roll.leaves > 0).map(p => {
              const behind = p.roll.planned - p.roll.actual;
              return (
                <div className="phase" key={p.task.id} title={`واقعی ${pct(p.roll.actual, 1)} · برنامه ${pct(p.roll.planned, 1)}`}>
                  <div><span>{p.task.name}</span><b>{pct(p.roll.actual, 1)}</b></div>
                  <Progress value={p.roll.actual} planned={p.roll.planned} />
                  <small>{toFa(p.roll.done)} از {toFa(p.roll.leaves)} تسک{p.roll.late > 0 && <em> · {toFa(p.roll.late)} عقب‌افتاده</em>}{behind > 0.05 && <> · {pct(behind, 1)} عقب</>}{p.roll.undated === p.roll.leaves && ' · بدون تاریخ'}</small>
                </div>
              );
            })}
          </div>
        </section>
        <div className="stack">
          <section className="card">
            <header className="card-head"><div><h2>وضعیت تسک‌ها</h2><p>{toFa(t.undated)} تسک هنوز تاریخ ندارد</p></div></header>
            <div className="segbar" role="img" aria-label="سهم وضعیت تسک‌ها">
              {segments.filter(s => s.value > 0).map(s => <i key={s.key} className={`seg-${s.key}`} style={{ flexGrow: s.value }} title={`${s.label}: ${toFa(s.value)}`} />)}
            </div>
            <div className="seglegend">{segments.map(s => <span key={s.key}><i className={`seg-${s.key}`} />{s.label}<b>{toFa(s.value)}</b></span>)}</div>
          </section>
          <section className="card grow">
            <header className="card-head"><div><h2>نیازمند توجه</h2><p>بیشترین تأخیر</p></div>{late.length > 0 && <button className="link" onClick={onGoTasks}>همه تسک‌ها<ArrowLeft size={15} /></button>}</header>
            {late.length === 0 ? <Empty icon={<CircleCheck size={20} />} title="تسک عقب‌افتاده‌ای نیست" /> : (
              <div className="late-list">
                {late.slice(0, 6).map(n => (
                  <button key={n.task.id} onClick={() => onOpenTask(n.task.id)}>
                    <span><strong>{n.task.name}</strong><small>{n.parent?.task.name}</small></span>
                    <em>{toFa(tree.today - n.roll.finish!)} روز</em>
                  </button>
                ))}
              </div>
            )}
          </section>
        </div>
      </div>

      <section className="card">
        <header className="card-head"><div><h2>زمان‌بندی فازها</h2><p>بازه برنامه و پیشرفت واقعی هر فاز</p></div></header>
        <Timeline phases={tree.roots} today={tree.today} />
      </section>
    </div>
  );
}
