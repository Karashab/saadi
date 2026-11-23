import { createContext, useContext, useEffect, useMemo, useRef, useState, type ReactNode } from 'react';
import { createPortal } from 'react-dom';
import { CalendarDays, ChevronLeft, ChevronRight, CircleAlert, CircleCheck, CircleDashed, LoaderCircle, X } from 'lucide-react';
import { MONTHS, STATUS_LABEL, isoToJalali, jalaliText, jalaliToIso, monthLength, toEn, toFa, todayIso, type Status } from './model';

type Toast = { id: number; text: string; bad?: boolean };
const ToastContext = createContext<(text: string, bad?: boolean) => void>(() => {});
export const useToast = () => useContext(ToastContext);

export function ToastProvider({ children }: { children: ReactNode }) {
  const [items, setItems] = useState<Toast[]>([]);
  const push = useMemo(() => (text: string, bad = false) => {
    const id = Date.now() + Math.random();
    setItems(list => [...list.slice(-2), { id, text, bad }]);
    setTimeout(() => setItems(list => list.filter(x => x.id !== id)), 4200);
  }, []);
  return (
    <ToastContext.Provider value={push}>
      {children}
      <div className="toasts" role="status" aria-live="polite">
        {items.map(t => <div key={t.id} className={`toast ${t.bad ? 'bad' : ''}`}>{t.bad ? <CircleAlert size={17} /> : <CircleCheck size={17} />}{t.text}</div>)}
      </div>
    </ToastContext.Provider>
  );
}

export function StatusBadge({ status, late }: { status: Status; late?: boolean }) {
  const key = late ? 'late' : status;
  const Icon = key === 'done' ? CircleCheck : key === 'late' ? CircleAlert : key === 'doing' ? LoaderCircle : CircleDashed;
  return <span className={`badge s-${key}`}><Icon size={13} aria-hidden />{STATUS_LABEL[key]}</span>;
}

export function Progress({ value, planned, tone }: { value: number; planned?: number; tone?: 'good' | 'bad' }) {
  return (
    <span className="meter" role="img" aria-label={`پیشرفت ${toFa(Math.round(value))} درصد`}>
      <i className={tone ?? ''} style={{ width: `${Math.min(100, value)}%` }} />
      {planned != null && planned > 0 && <b style={{ insetInlineStart: `${Math.min(100, planned)}%` }} />}
    </span>
  );
}

export function Modal({ title, onClose, children, wide }: { title: string; onClose: () => void; children: ReactNode; wide?: boolean }) {
  useEscape(onClose);
  return createPortal(
    <div className="overlay" onMouseDown={e => e.target === e.currentTarget && onClose()}>
      <section className={`modal ${wide ? 'wide' : ''}`} role="dialog" aria-modal="true" aria-label={title}>
        <header><h2>{title}</h2><button className="icon-btn" onClick={onClose} aria-label="بستن"><X size={18} /></button></header>
        <div className="modal-body">{children}</div>
      </section>
    </div>, document.body);
}

export function Drawer({ title, onClose, children, footer }: { title: ReactNode; onClose: () => void; children: ReactNode; footer?: ReactNode }) {
  useEscape(onClose);
  return createPortal(
    <div className="overlay drawer-overlay" onMouseDown={e => e.target === e.currentTarget && onClose()}>
      <aside className="drawer" role="dialog" aria-modal="true">
        <header><div className="drawer-title">{title}</div><button className="icon-btn" onClick={onClose} aria-label="بستن"><X size={18} /></button></header>
        <div className="drawer-body">{children}</div>
        {footer && <footer>{footer}</footer>}
      </aside>
    </div>, document.body);
}

function useEscape(action: () => void) {
  const latest = useRef(action);
  latest.current = action;
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => e.key === 'Escape' && latest.current();
    document.addEventListener('keydown', onKey);
    return () => document.removeEventListener('keydown', onKey);
  }, []);
}

export function Popover({ anchor, onClose, children }: { anchor: DOMRect; onClose: () => void; children: ReactNode }) {
  const ref = useRef<HTMLDivElement>(null);
  useEscape(onClose);
  useEffect(() => {
    const away = (e: MouseEvent) => { if (!ref.current?.contains(e.target as globalThis.Node)) onClose(); };
    const id = setTimeout(() => document.addEventListener('mousedown', away));
    return () => { clearTimeout(id); document.removeEventListener('mousedown', away); };
  }, [onClose]);
  const width = 256;
  const left = Math.max(10, Math.min(window.innerWidth - width - 10, anchor.left + anchor.width / 2 - width / 2));
  const below = anchor.bottom + 340 < window.innerHeight;
  return createPortal(
    <div ref={ref} className="popover" style={{ left, width, ...(below ? { top: anchor.bottom + 6 } : { bottom: window.innerHeight - anchor.top + 6 }) }}>{children}</div>,
    document.body);
}

export function JalaliInput({ value, onChange, label, disabled }: { value: string | null; onChange: (iso: string | null) => void; label: string; disabled?: boolean }) {
  const [anchor, setAnchor] = useState<DOMRect | null>(null);
  const seed = isoToJalali(value ?? todayIso());
  const [view, setView] = useState({ y: seed.jy, m: seed.jm });
  const open = (el: HTMLElement) => { const s = isoToJalali(value ?? todayIso()); setView({ y: s.jy, m: s.jm }); setAnchor(el.getBoundingClientRect()); };
  const move = (delta: number) => setView(v => { const t = v.y * 12 + v.m - 1 + delta; return { y: Math.floor(t / 12), m: (t % 12) + 1 }; });
  const firstIso = jalaliToIso(view.y, view.m, 1);
  const lead = (new Date(`${firstIso}T00:00:00Z`).getUTCDay() + 1) % 7;
  const days = monthLength(view.y, view.m);
  const today = todayIso();
  return (
    <div className="field">
      <label>{label}</label>
      <button type="button" className="input date-input" disabled={disabled} onClick={e => open(e.currentTarget)}>
        <CalendarDays size={15} /><span className={value ? '' : 'muted'}>{value ? jalaliText(value) : 'بدون تاریخ'}</span>
      </button>
      {anchor && (
        <Popover anchor={anchor} onClose={() => setAnchor(null)}>
          <div className="cal-head">
            <button type="button" className="icon-btn sm" onClick={() => move(-1)} aria-label="ماه قبل"><ChevronRight size={16} /></button>
            <strong>{MONTHS[view.m - 1]} {toFa(view.y)}</strong>
            <button type="button" className="icon-btn sm" onClick={() => move(1)} aria-label="ماه بعد"><ChevronLeft size={16} /></button>
          </div>
          <div className="cal-grid">
            {['ش', 'ی', 'د', 'س', 'چ', 'پ', 'ج'].map(d => <span key={d} className="cal-dow">{d}</span>)}
            {Array.from({ length: lead }, (_, i) => <span key={`x${i}`} />)}
            {Array.from({ length: days }, (_, i) => {
              const iso = jalaliToIso(view.y, view.m, i + 1);
              return <button type="button" key={iso} className={`${iso === value ? 'on' : ''} ${iso === today ? 'now' : ''}`} onClick={() => { onChange(iso); setAnchor(null); }}>{toFa(i + 1)}</button>;
            })}
          </div>
          {value && <button type="button" className="btn ghost full" onClick={() => { onChange(null); setAnchor(null); }}>حذف تاریخ</button>}
        </Popover>
      )}
    </div>
  );
}

export function PercentInput({ value, onChange, autoFocus }: { value: string; onChange: (v: string) => void; autoFocus?: boolean }) {
  return <input className="input num" inputMode="numeric" autoFocus={autoFocus} value={toFa(value)} placeholder="مثلاً ۴۰" aria-label="درصد پیشرفت"
    onChange={e => onChange(toEn(e.target.value).replace(/[^\d]/g, '').slice(0, 3))} />;
}

export const Empty = ({ icon, title, text }: { icon: ReactNode; title: string; text?: string }) => (
  <div className="empty"><span>{icon}</span><strong>{title}</strong>{text && <p>{text}</p>}</div>
);

export const Skeleton = ({ rows = 5 }: { rows?: number }) => (
  <div className="skeleton" aria-label="در حال بارگذاری">{Array.from({ length: rows }, (_, i) => <i key={i} />)}</div>
);
