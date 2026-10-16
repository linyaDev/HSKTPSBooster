#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
report_tree.py — build a self-contained, interactive HTML tree from an HSK TPS Booster
profiler dump.

The profiler (Ctrl+F9) writes report-<timestamp>-<light|full>/ folders with report.md,
methods.csv and defs.csv. This tool turns them into one static HTML page with two views:

  * "По методам"  — collapsible Category -> Mod -> Method tree (methods.csv).
  * "По вещам"    — collapsible Kind -> Mod -> Def tree (defs.csv): where the tick time
                    went by content (what a dispatcher like ThingWithComps.Tick ran for).

Note: the profiler stores EXCLUSIVE time per method and per-def attribution, but not the
call graph (who called whom), so true "methods called inside X" nesting is not in the data.
The "По вещам" view is the meaningful "what is inside the tick" answer.

No internet, no dependencies beyond the Python standard library.

Usage:
    python report_tree.py <report-folder-or-methods.csv> [output.html]
"""

import csv
import io
import json
import os
import re
import sys


def find_inputs(arg):
    """Return (methods_csv, defs_csv_or_None, report_md_or_None, dump_dir)."""
    if os.path.isdir(arg):
        d = arg
        mc = os.path.join(d, "methods.csv")
    else:
        mc = arg
        d = os.path.dirname(os.path.abspath(arg))
    dc = os.path.join(d, "defs.csv")
    rm = os.path.join(d, "report.md")
    return mc, (dc if os.path.isfile(dc) else None), (rm if os.path.isfile(rm) else None), d


def _num(r, key):
    try:
        return float(r.get(key) or 0)
    except (TypeError, ValueError):
        return 0.0


def read_methods(path):
    rows = []
    with io.open(path, encoding="utf-8") as f:
        for r in csv.DictReader(f):
            cat = (r.get("category") or "?").strip()
            if cat == "Calibration":
                continue
            rows.append({
                "c": cat,
                "m": (r.get("mod") or "?").strip(),
                "f": (r.get("family") or "").strip(),
                "n": (r.get("method") or "?").strip(),
                "e": (r.get("extra") or "").strip(),
                "ms": round(_num(r, "excl_ms_s"), 3),
                "cs": round(_num(r, "calls_s"), 1),
                "us": round(_num(r, "avg_excl_us_per_call"), 3),
                "w": round(_num(r, "worst_excl_ms"), 3),
            })
    return rows


def read_defs(path):
    if not path:
        return []
    rows = []
    with io.open(path, encoding="utf-8") as f:
        for r in csv.DictReader(f):
            rows.append({
                "c": (r.get("kind") or "?").strip(),       # top level = kind
                "m": (r.get("from_mod") or "?").strip(),    # mid level = mod
                "n": (r.get("def_name") or "?").strip(),    # leaf = def
                "f": "",
                "e": "",
                "ms": round(_num(r, "ms_s"), 3),
                "cs": round(_num(r, "calls_s"), 1),
                "us": round(_num(r, "avg_us_per_call"), 3),
                "w": round(_num(r, "worst_ms"), 3),
            })
    return rows


def read_meta(report_md):
    meta = {"capture": "", "fpstps": "", "speed": "", "recorded": ""}
    if not report_md:
        return meta
    try:
        text = io.open(report_md, encoding="utf-8").read()
    except OSError:
        return meta
    m = re.search(r"Recorded ([0-9: \-]+),", text)
    if m:
        meta["recorded"] = m.group(1).strip()
    m = re.search(r"Capture:\s*\*\*([^*]+)\*\*.*?,\s*([0-9]+)\s*frames,\s*([0-9]+)\s*ticks", text)
    if m:
        meta["capture"] = "%s · %s кадров · %s тиков" % (m.group(1).strip(), m.group(2), m.group(3))
    m = re.search(r"FPS avg ([0-9.]+)\*\*,\s*\*\*TPS avg ([0-9.]+)", text)
    if m:
        meta["fpstps"] = "FPS %s · TPS %s" % (m.group(1), m.group(2))
    m = re.search(r"Game speed:\s*([A-Za-z]+).*?tick rate multiplier\s*([0-9]+)", text)
    if m:
        meta["speed"] = "%s ×%s" % (m.group(1), m.group(2))
    return meta


def pack(rows):
    total = round(sum(x["ms"] for x in rows), 1)
    tops = {}
    for x in rows:
        tops[x["c"]] = tops.get(x["c"], 0) + x["ms"]
    return {"rows": rows, "total": total, "tops": {k: round(v, 1) for k, v in tops.items()}, "count": len(rows)}


def build(methods, defs, meta):
    data = {"methods": pack(methods), "defs": pack(defs), "meta": meta}
    payload = json.dumps(data, ensure_ascii=False, separators=(",", ":"))
    return TEMPLATE.replace("__PAYLOAD__", payload)


TEMPLATE = r"""<!doctype html><html lang="ru"><head><meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>HSK — дерево профиля кадра</title>
<style>
  :root{
    --bg:#f4f6f8;--panel:#fff;--ink:#1a2230;--ink-soft:#566074;--line:#dce1e8;--guide:#c3cad6;--accent:#3f6fb0;
    --sim:#c2711c;--sim-bg:#f8ebd8;--gui:#3f6fb0;--gui-bg:#e3ecf7;--har:#8a5bb0;--har-bg:#ece0f5;--upd:#2f8f6b;--upd-bg:#dcefe7;--mute:#6b7686;--mute-bg:#eceff3;--def:#3f6fb0;
    --mono:ui-monospace,"Cascadia Code","JetBrains Mono",Menlo,Consolas,monospace;
    --sans:ui-sans-serif,system-ui,"Segoe UI",Roboto,sans-serif;
  }
  @media (prefers-color-scheme:dark){:root{
    --bg:#10151c;--panel:#171e27;--ink:#e7ecf3;--ink-soft:#9aa6b6;--line:#273140;--guide:#34404f;--accent:#6ea8e6;
    --sim:#e8a04a;--sim-bg:#2a2114;--gui:#6ea8e6;--gui-bg:#152231;--har:#b58ad8;--har-bg:#20162b;--upd:#52c096;--upd-bg:#112820;--mute:#8a97a8;--mute-bg:#1c242e;--def:#6ea8e6;
  }}
  :root[data-theme="dark"]{--bg:#10151c;--panel:#171e27;--ink:#e7ecf3;--ink-soft:#9aa6b6;--line:#273140;--guide:#34404f;--accent:#6ea8e6;--sim:#e8a04a;--sim-bg:#2a2114;--gui:#6ea8e6;--gui-bg:#152231;--har:#b58ad8;--har-bg:#20162b;--upd:#52c096;--upd-bg:#112820;--mute:#8a97a8;--mute-bg:#1c242e;--def:#6ea8e6;}
  :root[data-theme="light"]{--bg:#f4f6f8;--panel:#fff;--ink:#1a2230;--ink-soft:#566074;--line:#dce1e8;--guide:#c3cad6;--accent:#3f6fb0;--sim:#c2711c;--sim-bg:#f8ebd8;--gui:#3f6fb0;--gui-bg:#e3ecf7;--har:#8a5bb0;--har-bg:#ece0f5;--upd:#2f8f6b;--upd-bg:#dcefe7;--mute:#6b7686;--mute-bg:#eceff3;--def:#3f6fb0;}
  *{box-sizing:border-box}
  body{margin:0;background:var(--bg);color:var(--ink);font-family:var(--sans);line-height:1.5;-webkit-font-smoothing:antialiased}
  .wrap{max-width:1100px;margin:0 auto;padding:32px 20px 64px}
  .eyebrow{font-size:12px;letter-spacing:.14em;text-transform:uppercase;color:var(--sim);font-weight:600;margin:0 0 6px}
  h1{font-size:clamp(22px,3.4vw,30px);margin:0 0 8px;line-height:1.15;letter-spacing:-.01em;text-wrap:balance}
  .lede{color:var(--ink-soft);max-width:68ch;margin:0;font-size:14.5px}
  .tabs{display:inline-flex;gap:3px;margin:20px 0 8px;background:var(--mute-bg);border-radius:10px;padding:3px}
  .tab{font-family:var(--sans);font-size:13px;font-weight:600;color:var(--ink-soft);background:transparent;border:0;border-radius:8px;padding:7px 15px;cursor:pointer}
  .tab[aria-selected="true"]{background:var(--panel);color:var(--ink);box-shadow:0 1px 2px rgba(0,0,0,.08)}
  .tab:focus-visible{outline:2px solid var(--accent);outline-offset:1px}
  .bar-sum{display:flex;flex-wrap:wrap;gap:8px;margin:6px 0}
  .sum{display:inline-flex;align-items:baseline;gap:7px;font-size:12.5px;font-weight:500;padding:6px 12px;border-radius:999px;border:1px solid var(--line);background:var(--panel);color:var(--ink-soft)}
  .sum b{font-variant-numeric:tabular-nums;color:var(--ink);font-size:14px}
  .dot{width:9px;height:9px;border-radius:50%;display:inline-block}
  .d-Tick{background:var(--sim)}.d-Update{background:var(--upd)}.d-GUI{background:var(--gui)}.d-Harmony{background:var(--har)}.d-def{background:var(--def)}
  .controls{display:flex;flex-wrap:wrap;gap:10px;align-items:center;margin:14px 0 12px}
  .search{flex:1;min-width:220px;display:flex;align-items:center;gap:8px;background:var(--panel);border:1px solid var(--line);border-radius:9px;padding:8px 12px}
  .search input{border:0;background:transparent;color:var(--ink);font-family:var(--sans);font-size:14px;width:100%;outline:none}
  .search svg{flex:none;opacity:.5}
  .btn{font-family:var(--sans);font-size:12.5px;font-weight:600;color:var(--ink-soft);background:var(--panel);border:1px solid var(--line);border-radius:8px;padding:8px 12px;cursor:pointer}
  .btn:hover{color:var(--ink);border-color:var(--guide)}
  .btn:focus-visible,.search:focus-within{outline:2px solid var(--accent);outline-offset:1px}
  select.btn{appearance:none}
  #tree{background:var(--panel);border:1px solid var(--line);border-radius:12px;padding:6px;overflow-x:auto}
  .row{display:grid;grid-template-columns:1fr 150px 92px 84px;align-items:center;gap:10px;border-radius:7px;padding:4px 10px;min-width:620px}
  .row.tog{cursor:pointer}
  .row:hover{background:color-mix(in srgb,var(--accent) 7%,transparent)}
  .lhs{display:flex;align-items:center;gap:6px;min-width:0}
  .tw{width:14px;flex:none;text-align:center;color:var(--ink-soft);font-family:var(--mono);font-size:11px}
  .name{font-family:var(--mono);font-size:13px;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}
  .cnt{font-family:var(--sans);font-size:11px;color:var(--ink-soft);background:var(--mute-bg);padding:1px 7px;border-radius:999px;flex:none}
  .meter{height:16px;border-radius:4px;background:var(--mute-bg);position:relative;overflow:hidden}
  .meter i{position:absolute;inset:0 auto 0 0;border-radius:4px;background:var(--def)}
  .num{font-variant-numeric:tabular-nums;font-size:12px;text-align:right;color:var(--ink-soft)}
  .num.ms{font-weight:600;color:var(--ink)}
  .lvl-cat .name{font-weight:700;font-family:var(--sans);font-size:14px}
  .lvl-mod .name{font-weight:600}
  .z-Tick i{background:var(--sim)} .z-Update i{background:var(--upd)} .z-GUI i{background:var(--gui)} .z-Harmony i{background:var(--har)}
  .z-Tick.lvl-cat{border-left:3px solid var(--sim)} .z-Update.lvl-cat{border-left:3px solid var(--upd)} .z-GUI.lvl-cat{border-left:3px solid var(--gui)} .z-Harmony.lvl-cat{border-left:3px solid var(--har)}
  .lvl-cat.z-def{border-left:3px solid var(--def)}
  .kids{overflow:hidden}
  .hint{font-size:12px;color:var(--ink-soft);margin:10px 2px 0}
  .colhead{display:grid;grid-template-columns:1fr 150px 92px 84px;gap:10px;padding:2px 10px 8px;font-size:10.5px;letter-spacing:.05em;text-transform:uppercase;color:var(--ink-soft);min-width:620px}
  .colhead span:nth-child(n+2){text-align:right}
  .empty{padding:24px;text-align:center;color:var(--ink-soft);font-size:14px}
</style></head><body>

<div class="wrap">
  <p class="eyebrow" id="eyebrow">HSK TPS Booster · профиль</p>
  <h1>Дерево профиля кадра</h1>
  <p class="lede" id="lede"></p>

  <div class="tabs" role="tablist">
    <button class="tab" id="tab-methods" role="tab" aria-selected="true">По методам</button>
    <button class="tab" id="tab-defs" role="tab" aria-selected="false">По вещам (дефам)</button>
  </div>

  <div class="bar-sum" id="sums"></div>

  <div class="controls">
    <label class="search">
      <svg width="15" height="15" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><circle cx="11" cy="11" r="7"/><path d="m21 21-4.3-4.3"/></svg>
      <input id="q" type="text" placeholder="Поиск…" autocomplete="off">
    </label>
    <select class="btn" id="sort">
      <option value="ms">Сортировать: по времени</option>
      <option value="cs">по вызовам/с</option>
      <option value="us">по мкс/вызов</option>
      <option value="w">по худшему вызову</option>
    </select>
    <button class="btn" id="expand">Развернуть всё</button>
    <button class="btn" id="collapse">Свернуть всё</button>
  </div>

  <div class="colhead"><span id="col1">метод / мод</span><span>мс/с (доля)</span><span>вызовов/с</span><span>мкс·худш</span></div>
  <div id="tree"></div>
  <p class="hint" id="hint"></p>
</div>

<script id="data" type="application/json">__PAYLOAD__</script>
<script>
const D = JSON.parse(document.getElementById("data").textContent);
const fmt = (v,d=2)=> (v||0).toLocaleString("ru-RU",{minimumFractionDigits:d,maximumFractionDigits:d});
const fmt0 = v=> Math.round(v||0).toLocaleString("ru-RU");
const CATORDER = ["Tick","Update","GUI","Harmony"];

const eb = [D.meta&&D.meta.speed, D.meta&&D.meta.capture, D.meta&&D.meta.fpstps].filter(Boolean).join(" · ");
if(eb) document.getElementById("eyebrow").textContent = "HSK TPS Booster · " + eb;

let view = "methods", sortKey = "ms", query = "";

const LEDE = {
  methods: "Все хуки, что реально сработали за запись (из установленных остальные ни разу не вызвались). Группировка Категория → Мод → Метод. Время — эксклюзивное мс/с с вычтенным оверхедом пробы.",
  defs: "Куда ушло тиковое время по содержимому — на какие вещи, хедиффы и джобы. Это и есть «что внутри» диспетчеров вроде ThingWithComps.Tick. Группировка Тип дефа → Мод → Деф. (Профиль не хранит рёбра вызовов, поэтому это привязка по дефам, а не стек.)"
};
const HINT = {
  methods: "Доля в полосе — относительно самой дорогой строки её уровня. «мкс·худш» = среднее мкс/вызов; при наведении — худший единичный вызов, мс.",
  defs: "Учтены только хуки на Thing / ThingComp / Hediff / HediffComp (+ JobDriver.DriverTick по JobDef). Доля — относительно самого дорогого соседа уровня."
};

function set(){
  const dd = D[view];
  document.getElementById("lede").textContent = LEDE[view];
  document.getElementById("hint").textContent = HINT[view];
  document.getElementById("col1").textContent = view==="methods" ? "метод / мод" : "деф / мод";
  document.getElementById("tab-methods").setAttribute("aria-selected", view==="methods");
  document.getElementById("tab-defs").setAttribute("aria-selected", view==="defs");
  const tops = dd.tops;
  const order = view==="methods" ? CATORDER.filter(c=>tops[c]!=null) : Object.keys(tops).sort((a,b)=>tops[b]-tops[a]);
  const sums = document.getElementById("sums");
  sums.innerHTML = `<span class="sum"><span class="dot" style="background:var(--ink)"></span>всего <b>${fmt(dd.total,0)}</b> мс/с · ${fmt0(dd.count)} ${view==="methods"?"методов":"дефов"}</span>` +
    order.map(c=>`<span class="sum"><span class="dot ${view==="methods"?("d-"+c):"d-def"}"></span>${c} <b>${fmt(tops[c],0)}</b></span>`).join("");
}

const sum=(a,k)=>a.reduce((s,x)=>s+(x[k]||0),0);
const maxOf=(a,k)=>a.reduce((s,x)=>Math.max(s,x[k]||0),0);
function esc(s){return (s||"").replace(/[&<>]/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;'}[c]));}
function group(rows){const b=new Map();for(const r of rows){if(!b.has(r.c))b.set(r.c,new Map());const mm=b.get(r.c);if(!mm.has(r.m))mm.set(r.m,[]);mm.get(r.m).push(r);}return b;}
function zcls(c){return view==="methods" ? ("z-"+c) : "z-def";}
function meter(val,max){const pct=max>0?Math.max(1.5,val/max*100):0;return `<div class="meter"><i style="width:${pct}%"></i></div>`;}

function render(){
  const dd = D[view];
  const tree=document.getElementById("tree");
  const rows=query?dd.rows.filter(r=>(r.n+" "+r.m+" "+(r.e||"")).toLowerCase().includes(query)):dd.rows;
  if(!rows.length){tree.innerHTML=`<div class="empty">Ничего не найдено по «${esc(query)}»</div>`;return;}
  const byTop=group(rows);
  let tops=[...byTop.keys()];
  if(view==="methods") tops.sort((a,b)=>{const ia=CATORDER.indexOf(a),ib=CATORDER.indexOf(b);return (ia<0?9:ia)-(ib<0?9:ib);});
  else tops.sort((a,b)=>sum([].concat(...byTop.get(b).values()),"ms")-sum([].concat(...byTop.get(a).values()),"ms"));
  let html="";
  for(const c of tops){
    const mm=byTop.get(c);
    const allRows=[].concat(...mm.values());
    const topMs=sum(allRows,"ms");
    const mods=[...mm.keys()].map(m=>({m,rows:mm.get(m),ms:sum(mm.get(m),"ms")})).sort((a,b)=>b.ms-a.ms);
    const topMax=maxOf(mods,"ms");
    const open=query?true:(view==="methods"?c==="Tick":tops.indexOf(c)===0);
    const cl=zcls(c);
    html+=`<div class="row tog lvl-cat ${cl}" aria-expanded="${open}"><div class="lhs"><span class="tw">${open?"▼":"▶"}</span><span class="name">${esc(c)}</span><span class="cnt">${allRows.length}</span></div>${meter(topMs,Math.max(topMax,topMs))}<div class="num ms">${fmt(topMs)}</div><div class="num"></div></div>`;
    html+=`<div class="kids" data-kids style="${open?"":"display:none"}">`;
    for(const md of mods){
      const mrows=md.rows.slice().sort((a,b)=>(b[sortKey]||0)-(a[sortKey]||0));
      const mMax=maxOf(mrows,"ms");
      const mopen=!!query;
      html+=`<div class="row tog lvl-mod ${cl}" style="padding-left:26px" aria-expanded="${mopen}"><div class="lhs"><span class="tw">${mopen?"▼":"▶"}</span><span class="name">${esc(md.m)}</span><span class="cnt">${mrows.length}</span></div>${meter(md.ms,topMax<=0?md.ms:topMax)}<div class="num ms">${fmt(md.ms)}</div><div class="num"></div></div>`;
      html+=`<div class="kids" data-kids style="${mopen?"":"display:none"}">`;
      for(const r of mrows){
        const sec=sortKey==="cs"?fmt0(r.cs):sortKey==="us"?fmt(r.us):sortKey==="w"?fmt(r.w):fmt0(r.cs);
        const tip=(r.f||r.w)?` title="${esc(r.f||"")}${r.e?" · "+esc(r.e):""}${r.w?" · худший вызов "+fmt(r.w)+" мс":""}"`:"";
        html+=`<div class="row lvl-mth ${cl}" style="padding-left:46px"${tip}><div class="lhs"><span class="tw"></span><span class="name">${esc(r.n)}</span></div>${meter(r.ms,mMax<=0?r.ms:mMax)}<div class="num ms">${fmt(r.ms)}</div><div class="num">${sortKey==="ms"?fmt0(r.cs):sec}</div></div>`;
      }
      html+=`</div>`;
    }
    html+=`</div>`;
  }
  tree.innerHTML=html;
}

document.getElementById("tree").addEventListener("click",e=>{
  const row=e.target.closest(".row.tog");if(!row)return;
  const kids=row.nextElementSibling;if(!kids||!kids.hasAttribute("data-kids"))return;
  const open=kids.style.display==="none";kids.style.display=open?"":"none";
  row.setAttribute("aria-expanded",open);const tw=row.querySelector(".tw");if(tw)tw.textContent=open?"▼":"▶";
});
function switchView(v){ if(v===view)return; view=v; set(); render(); }
document.getElementById("tab-methods").addEventListener("click",()=>switchView("methods"));
document.getElementById("tab-defs").addEventListener("click",()=>switchView("defs"));
let t;
document.getElementById("q").addEventListener("input",e=>{clearTimeout(t);t=setTimeout(()=>{query=e.target.value.trim().toLowerCase();render();},120);});
document.getElementById("sort").addEventListener("change",e=>{sortKey=e.target.value;render();});
document.getElementById("expand").addEventListener("click",()=>{document.querySelectorAll("#tree [data-kids]").forEach(k=>k.style.display="");document.querySelectorAll("#tree .row.tog").forEach(r=>{r.setAttribute("aria-expanded","true");const tw=r.querySelector(".tw");if(tw)tw.textContent="▼";});});
document.getElementById("collapse").addEventListener("click",()=>{query=document.getElementById("q").value.trim().toLowerCase();render();});

if(!D.defs || !D.defs.count) document.getElementById("tab-defs").style.display="none";
set(); render();
</script>
</body></html>
"""


def main(argv):
    if len(argv) < 2:
        print(__doc__)
        return 2
    methods_csv, defs_csv, report_md, dump_dir = find_inputs(argv[1])
    if not os.path.isfile(methods_csv):
        print("methods.csv not found at: %s" % methods_csv)
        return 1
    out = argv[2] if len(argv) > 2 else os.path.join(dump_dir, "report-tree.html")

    methods = read_methods(methods_csv)
    defs = read_defs(defs_csv)
    if not methods:
        print("no method rows in %s" % methods_csv)
        return 1
    html = build(methods, defs, read_meta(report_md))
    with io.open(out, "w", encoding="utf-8") as f:
        f.write(html)

    mt = round(sum(x["ms"] for x in methods), 1)
    print("OK: %d methods (%.1f ms/s), %d defs -> %s" % (len(methods), mt, len(defs), out))
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
