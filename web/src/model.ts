import { toGregorian, toJalaali, jalaaliMonthLength } from 'jalaali-js';
import type { Ancestor, Role, Task } from './api';

export const toFa = (v: string | number) => String(v).replace(/\d/g, d => '۰۱۲۳۴۵۶۷۸۹'[+d]).replace(/\./g, '٫');
export const toEn = (v: string) => v.replace(/[۰-۹]/g, d => String('۰۱۲۳۴۵۶۷۸۹'.indexOf(d))).replace(/[٠-٩]/g, d => String('٠١٢٣٤٥٦٧٨٩'.indexOf(d)));
export const pct = (v: number, digits = 0) => `${toFa(v.toFixed(digits))}٪`;

const pad = (n: number) => String(n).padStart(2, '0');
const parse = (iso: string) => { const [y, m, d] = iso.split('-').map(Number); return { y, m, d }; };
export const dayNumber = (iso: string) => { const { y, m, d } = parse(iso); return Math.floor(Date.UTC(y, m - 1, d) / 86400000); };
export const isoFromDay = (day: number) => new Date(day * 86400000).toISOString().slice(0, 10);
export const todayIso = () => new Intl.DateTimeFormat('en-CA', { timeZone: 'Asia/Tehran' }).format(new Date());

export const isoToJalali = (iso: string) => { const { y, m, d } = parse(iso); return toJalaali(y, m, d); };
export const jalaliToIso = (jy: number, jm: number, jd: number) => { const g = toGregorian(jy, jm, jd); return `${g.gy}-${pad(g.gm)}-${pad(g.gd)}`; };
export const jalaliText = (iso: string | null | undefined) => {
  if (!iso) return '—';
  const j = isoToJalali(iso.slice(0, 10));
  return toFa(`${j.jy}/${pad(j.jm)}/${pad(j.jd)}`);
};
export const MONTHS = ['فروردین', 'اردیبهشت', 'خرداد', 'تیر', 'مرداد', 'شهریور', 'مهر', 'آبان', 'آذر', 'دی', 'بهمن', 'اسفند'];
export const monthLength = jalaaliMonthLength;
export const longDate = (iso: string) => { const j = isoToJalali(iso); return `${toFa(j.jd)} ${MONTHS[j.jm - 1]} ${toFa(j.jy)}`; };

export type Status = 'todo' | 'doing' | 'done';
export const STATUS_LABEL: Record<Status | 'late', string> = { todo: 'شروع نشده', doing: 'در حال انجام', done: 'تکمیل شده', late: 'عقب‌افتاده' };

export interface Roll { start: number | null; finish: number | null; weight: number; actual: number; planned: number; leaves: number; done: number; doing: number; late: number; undated: number; isLate: boolean }
export interface Node { task: Task; children: Node[]; depth: number; roll: Roll; parent: Node | null }

const plannedAt = (s: number | null, f: number | null, today: number) => {
  if (s == null || f == null || today < s) return 0;
  return today >= f ? 100 : ((today - s + 1) / (f - s + 1)) * 100;
};

function rollup(node: Node, today: number): Roll {
  if (!node.children.length) {
    const t = node.task;
    const s = t.start_date ? dayNumber(t.start_date) : null;
    const f = t.finish_date ? dayNumber(t.finish_date) : null;
    const p = Math.min(100, Math.max(0, Number(t.percent_complete ?? 0)));
    const late = f != null && f < today && p < 100;
    const weight = Number(t.duration_days) > 0 ? Number(t.duration_days) : s != null && f != null ? Math.max(1, f - s + 1) : 1;
    if (t.headingOnly) return (node.roll = { start: null, finish: null, weight: 0, actual: 0, planned: 0, leaves: 0, done: 0, doing: 0, late: 0, undated: 0, isLate: false });
    return (node.roll = { start: s, finish: f, weight, actual: p, planned: plannedAt(s, f, today), leaves: 1, done: p >= 100 ? 1 : 0, doing: p > 0 && p < 100 ? 1 : 0, late: late ? 1 : 0, undated: f == null ? 1 : 0, isLate: late });
  }
  const r: Roll = { start: null, finish: null, weight: 0, actual: 0, planned: 0, leaves: 0, done: 0, doing: 0, late: 0, undated: 0, isLate: false };
  for (const child of node.children) {
    const x = rollup(child, today);
    if (x.start != null) r.start = r.start == null ? x.start : Math.min(r.start, x.start);
    if (x.finish != null) r.finish = r.finish == null ? x.finish : Math.max(r.finish, x.finish);
    r.weight += x.weight; r.actual += x.actual * x.weight; r.planned += x.planned * x.weight;
    r.leaves += x.leaves; r.done += x.done; r.doing += x.doing; r.late += x.late; r.undated += x.undated;
  }
  if (r.weight) { r.actual /= r.weight; r.planned /= r.weight; }
  r.isLate = r.finish != null && r.finish < today && r.actual < 99.95;
  return (node.roll = r);
}

const emptyRoll = (): Roll => ({ start: null, finish: null, weight: 0, actual: 0, planned: 0, leaves: 0, done: 0, doing: 0, late: 0, undated: 0, isLate: false });

export function buildTree(tasks: Task[], ancestors: Ancestor[]) {
  const today = dayNumber(todayIso());
  const heading = ancestors.map<Task>(a => ({
    id: a.id, parent_id: a.parent_id, wbs: a.wbs, name: a.name, sort_order: a.sort_order, organization_id: null, is_summary: true,
    start_date: null, finish_date: null, duration_days: null, percent_complete: null, priority: null, assignee_text: null,
    predecessors: null, notes: null, source_info: null, version: 0, headingOnly: true,
  }));
  const nodes = new Map<string, Node>();
  for (const task of [...heading, ...tasks]) nodes.set(task.id, { task, children: [], depth: 0, roll: emptyRoll(), parent: null });
  const roots: Node[] = [];
  for (const node of nodes.values()) {
    const parent = node.task.parent_id ? nodes.get(node.task.parent_id) : undefined;
    if (parent) { parent.children.push(node); node.parent = parent; } else roots.push(node);
  }
  const order = (a: Node, b: Node) => a.task.sort_order - b.task.sort_order;
  const settle = (list: Node[], depth: number) => { list.sort(order); for (const n of list) { n.depth = depth; settle(n.children, depth + 1); } };
  settle(roots, 0);
  roots.forEach(root => rollup(root, today));
  const total = emptyRoll();
  for (const { roll: x } of roots) {
    total.weight += x.weight; total.actual += x.actual * x.weight; total.planned += x.planned * x.weight;
    total.leaves += x.leaves; total.done += x.done; total.doing += x.doing; total.late += x.late; total.undated += x.undated;
    if (x.start != null) total.start = total.start == null ? x.start : Math.min(total.start, x.start);
    if (x.finish != null) total.finish = total.finish == null ? x.finish : Math.max(total.finish, x.finish);
  }
  if (total.weight) { total.actual /= total.weight; total.planned /= total.weight; }
  return { roots, nodes, total, today };
}

export const statusOf = (roll: Roll): Status => (roll.actual >= 99.95 ? 'done' : roll.actual > 0 ? 'doing' : 'todo');

export const ROLE_LABEL: Record<Role, string> = {
  system_admin: 'مدیر سیستم', project_manager: 'مدیر پروژه', company_admin: 'مدیر شرکت', executor: 'کارشناس مجری', viewer: 'ناظر',
};
export const isManager = (role: Role) => role === 'system_admin' || role === 'project_manager';
export const canEdit = (role: Role) => role !== 'viewer';
export const canCreate = (role: Role) => isManager(role) || role === 'company_admin';
export const canDeleteItems = canCreate;

export const fileSize = (bytes: number) => {
  if (bytes < 1024) return `${toFa(bytes)} بایت`;
  if (bytes < 1048576) return `${toFa((bytes / 1024).toFixed(0))} کیلوبایت`;
  if (bytes < 1073741824) return `${toFa((bytes / 1048576).toFixed(1))} مگابایت`;
  return `${toFa((bytes / 1073741824).toFixed(2))} گیگابایت`;
};
