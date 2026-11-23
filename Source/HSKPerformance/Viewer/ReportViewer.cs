using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace HSKPerformance
{
    /// <summary>Opens the viewer: after a recording (requested by the session, opened from the driver's Update), by Ctrl+F11 or from the settings.</summary>
    public static class ReportViewer
    {
        static string pending;

        public static void RequestOpen(string dir) { pending = dir; }

        /// <summary>Called every frame by the driver; opens a requested report once a window can be shown.</summary>
        public static void Pump()
        {
            if (pending == null || !CanOpen()) return;
            string d = pending;
            pending = null;
            Open(d);
        }

        static bool CanOpen()
        {
            try
            {
                if (LongEventHandler.ShouldWaitForEvent) return false;
                var s = Current.ProgramState;
                return (s == ProgramState.Playing || s == ProgramState.Entry) && Find.WindowStack != null;
            }
            catch { return false; }
        }

        /// <summary>Newest report, or the window with a "no reports yet" note.</summary>
        public static void OpenLatest()
        {
            var l = ReportData.ListReports();
            Open(l.Count > 0 ? l[0] : null);
        }

        public static void Open(string dir)
        {
            if (!CanOpen()) return;
            var w = Find.WindowStack.WindowOfType<Dialog_ReportViewer>();
            if (w != null) { if (dir != null) w.Select(dir); return; }
            Find.WindowStack.Add(new Dialog_ReportViewer(dir));
        }

        public static void Toggle()
        {
            if (!CanOpen()) return;
            if (Find.WindowStack.TryRemove(typeof(Dialog_ReportViewer))) return;
            OpenLatest();
        }
    }

    /// <summary>
    /// In-game profiler report: headline numbers, frame budget, frame / tick / load charts, cost per mod and the
    /// Category → Mod → Method and Kind → Mod → Def trees. Everything is read from the dump folder, so older reports open too.
    /// </summary>
    public sealed class Dialog_ReportViewer : Window
    {
        // ------------------------------------------------------------------ palette (same hues as Tools/report_tree.py)
        static readonly Color CTick = new Color(0.89f, 0.59f, 0.25f);
        static readonly Color CUpdate = new Color(0.32f, 0.75f, 0.56f);
        static readonly Color CGui = new Color(0.43f, 0.64f, 0.90f);
        static readonly Color CHarmony = new Color(0.71f, 0.54f, 0.85f);
        static readonly Color CRest = new Color(0.47f, 0.51f, 0.57f);
        static readonly Color CDef = new Color(0.43f, 0.64f, 0.90f);
        static readonly Color CWarn = new Color(0.95f, 0.80f, 0.30f);
        static readonly Color CBad = new Color(0.95f, 0.55f, 0.25f);
        static readonly Color CAwful = new Color(0.92f, 0.30f, 0.28f);
        static readonly Color CTrack = new Color(1f, 1f, 1f, 0.06f);
        static readonly Color CGrid = new Color(1f, 1f, 1f, 0.10f);
        static readonly Color CSoft = new Color(0.68f, 0.70f, 0.74f);
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        static Color CatColor(string cat)
        {
            switch (cat)
            {
                case "Tick": return CTick;
                case "Update": return CUpdate;
                case "GUI": return CGui;
                case "Harmony": return CHarmony;
                default: return CDef;
            }
        }

        static string CatLabel(string cat)
        {
            switch (cat)
            {
                case "Tick": case "Update": case "GUI": case "Harmony": return ("HSKTPS.Viewer.Cat." + cat).Translate();
                default: return cat;
            }
        }

        static string F(double v, int d) { return double.IsNaN(v) ? "—" : v.ToString("F" + d, Inv); }

        // ------------------------------------------------------------------ state

        enum Tab { Overview, Mods, Methods, Defs }

        ReportData data;
        List<string> reports;
        Tab tab = Tab.Overview;
        Vector2 overviewScroll, modsScroll;
        readonly TreeView methodsTree = new TreeView(true);
        readonly TreeView defsTree = new TreeView(false);
        string hoverText;
        Vector2 overviewHeight = new Vector2(0f, 900f);

        public override Vector2 InitialSize { get { return new Vector2(Mathf.Min(UI.screenWidth - 40f, 1240f), Mathf.Min(UI.screenHeight - 40f, 880f)); } }

        public Dialog_ReportViewer(string dir)
        {
            doCloseX = true;
            draggable = true;
            resizeable = true;
            closeOnClickedOutside = false;
            closeOnAccept = false;            // Enter in the search field must not close the window
            absorbInputAroundWindow = false;
            forcePause = false;
            preventCameraMotion = false;
            onlyOneOfTypeAllowed = true;
            reports = ReportData.ListReports();
            if (dir != null) Select(dir);
        }

        public void Select(string dir)
        {
            data = ReportData.Load(dir);
            methodsTree.SetRows(data.Methods);
            defsTree.SetRows(data.Defs);
            overviewScroll = modsScroll = Vector2.zero;
            reports = ReportData.ListReports();
        }

        // ------------------------------------------------------------------ window

        public override void DoWindowContents(Rect inRect)
        {
            hoverText = null;
            Text.Font = GameFont.Small;
            Text.Anchor = TextAnchor.UpperLeft;
            GUI.color = Color.white;
            try
            {
                float y = inRect.y;
                DrawTopBar(new Rect(inRect.x, y, inRect.width - 30f, 30f));
                y += 34f;
                if (data == null)
                {
                    Widgets.Label(new Rect(inRect.x, y + 10f, inRect.width, 60f), "HSKTPS.Viewer.NoReports".Translate());
                    return;
                }
                if (data.Error != null)
                {
                    GUI.color = CAwful;
                    Widgets.Label(new Rect(inRect.x, y, inRect.width, 24f), "HSKTPS.Viewer.LoadFailed".Translate(data.Error));
                    GUI.color = Color.white;
                    y += 24f;
                }
                Text.Font = GameFont.Tiny;
                GUI.color = CSoft;
                Widgets.Label(new Rect(inRect.x, y, inRect.width, 20f), HeaderLine());
                GUI.color = Color.white;
                Text.Font = GameFont.Small;
                y += 22f + 32f; // header line, then room for the tabs (TabDrawer draws above its rect)

                var tabs = new List<TabRecord>
                {
                    new TabRecord("HSKTPS.Viewer.Tab.Overview".Translate(), () => tab = Tab.Overview, tab == Tab.Overview),
                    new TabRecord("HSKTPS.Viewer.Tab.Mods".Translate(), () => tab = Tab.Mods, tab == Tab.Mods),
                    new TabRecord("HSKTPS.Viewer.Tab.Methods".Translate(), () => tab = Tab.Methods, tab == Tab.Methods),
                    new TabRecord("HSKTPS.Viewer.Tab.Defs".Translate(), () => tab = Tab.Defs, tab == Tab.Defs),
                };
                var body = new Rect(inRect.x, y, inRect.width, inRect.yMax - y);
                Widgets.DrawMenuSection(body);
                TabDrawer.DrawTabs(body, tabs, 220f);
                body = body.ContractedBy(10f);

                switch (tab)
                {
                    case Tab.Overview: DrawOverview(body); break;
                    case Tab.Mods: DrawMods(body); break;
                    case Tab.Methods: methodsTree.Draw(body, data, this); break;
                    case Tab.Defs: defsTree.Draw(body, data, this); break;
                }
            }
            finally
            {
                Text.Font = GameFont.Small;
                Text.Anchor = TextAnchor.UpperLeft;
                GUI.color = Color.white;
            }
            if (hoverText != null) DrawHoverBox(hoverText);
        }

        void DrawTopBar(Rect r)
        {
            string label = data != null ? ReportData.Label(data.Dir) : "HSKTPS.Viewer.NoReportSelected".Translate().ToString();
            if (Widgets.ButtonText(new Rect(r.x, r.y, 300f, r.height), label + "  ▾"))
            {
                reports = ReportData.ListReports();
                var opts = new List<FloatMenuOption>();
                foreach (var d in reports.Take(60))
                {
                    string dd = d;
                    opts.Add(new FloatMenuOption(ReportData.Label(d), () => Select(dd)));
                }
                if (opts.Count == 0) opts.Add(new FloatMenuOption("HSKTPS.Viewer.NoReports".Translate(), null));
                Find.WindowStack.Add(new FloatMenu(opts));
            }
            float x = r.x + 308f;
            if (Widgets.ButtonText(new Rect(x, r.y, 160f, r.height), "HSKTPS.Viewer.OpenFolder".Translate()))
            {
                try { Application.OpenURL(data != null ? data.Dir : ProbeConfig.Dir); } catch { }
            }
            x += 168f;
            Text.Font = GameFont.Tiny;
            GUI.color = CSoft;
            Text.Anchor = TextAnchor.MiddleLeft;
            Widgets.Label(new Rect(x, r.y, r.xMax - x, r.height), "HSKTPS.Viewer.Hotkeys".Translate());
            Text.Anchor = TextAnchor.UpperLeft;
            GUI.color = Color.white;
            Text.Font = GameFont.Small;
        }

        string HeaderLine()
        {
            var parts = new List<string>();
            double cap = data.Num("capture_s");
            if (!double.IsNaN(cap)) parts.Add("HSKTPS.Viewer.Captured".Translate(F(cap, 1), F(data.Num("frames"), 0), F(data.Num("ticks"), 0)));
            string speed = MostCommonSpeed();
            if (speed != null) parts.Add("HSKTPS.Viewer.Speed".Translate(speed));
            string stop = data.Str("stop_reason");
            if (!string.IsNullOrEmpty(stop)) parts.Add("HSKTPS.Viewer.StoppedBy".Translate(stop));
            return string.Join("   ·   ", parts);
        }

        string MostCommonSpeed()
        {
            if (data.Timeline.Count == 0) return null;
            return data.Timeline.GroupBy(s => s.Speed).OrderByDescending(g => g.Count()).First().Key;
        }

        // ------------------------------------------------------------------ overview

        void DrawOverview(Rect r)
        {
            var view = new Rect(0f, 0f, r.width - 16f, overviewHeight.y);
            Widgets.BeginScrollView(r, ref overviewScroll, view);
            float y = 0f, w = view.width;

            y = DrawTiles(new Rect(0f, y, w, 58f));
            y += 14f;
            y = DrawBudget(0f, y, w);
            y += 16f;

            if (data.FrameMs.Length > 0) y = DrawFrameChart(new Rect(0f, y, w, 170f));
            else if (data.Timeline.Count > 0) y = DrawFrameChartFallback(new Rect(0f, y, w, 170f));
            y += 16f;
            if (data.Timeline.Count > 0)
            {
                y = DrawTpsChart(new Rect(0f, y, w, 130f));
                y += 16f;
                y = DrawLoadChart(new Rect(0f, y, w, 170f));
                y += 16f;
            }
            if (data.Context.Count > 0)
            {
                Text.Font = GameFont.Small;
                Widgets.Label(new Rect(0f, y, w, 24f), "HSKTPS.Viewer.Context".Translate());
                y += 24f;
                Text.Font = GameFont.Tiny;
                GUI.color = CSoft;
                foreach (var c in data.Context)
                {
                    float h = Text.CalcHeight(c, w);
                    Widgets.Label(new Rect(0f, y, w, h), c);
                    y += h;
                }
                GUI.color = Color.white;
                Text.Font = GameFont.Small;
            }
            overviewHeight.y = y + 10f;
            Widgets.EndScrollView();
        }

        float DrawTiles(Rect r)
        {
            double p99 = data.Num("frame_p99_ms");
            var tiles = new List<(string val, string cap, Color col)>
            {
                (F(data.Num("tps"), 0), "HSKTPS.Viewer.Tile.Tps".Translate(), Color.white),
                (F(data.Num("fps"), 1), "HSKTPS.Viewer.Tile.Fps".Translate(), Color.white),
                (F(data.Num("frame_avg_ms"), 1), "HSKTPS.Viewer.Tile.FrameAvg".Translate(), Color.white),
                (F(p99, 1), "HSKTPS.Viewer.Tile.FrameP99".Translate(), double.IsNaN(p99) ? Color.white : p99 > 100 ? CAwful : p99 > 50 ? CBad : p99 > 33.4 ? CWarn : Color.white),
                (F(data.Num("tick_avg_ms"), 2), "HSKTPS.Viewer.Tile.TickAvg".Translate(), Color.white),
                (F(data.Num("tick_worst_ms"), 1), "HSKTPS.Viewer.Tile.TickWorst".Translate(), Color.white),
                (F(data.Num("overhead_ms_s"), 0), "HSKTPS.Viewer.Tile.Overhead".Translate(), CSoft),
            };
            float gap = 8f, tw = (r.width - gap * (tiles.Count - 1)) / tiles.Count;
            for (int i = 0; i < tiles.Count; i++)
            {
                var t = new Rect(r.x + i * (tw + gap), r.y, tw, r.height);
                Widgets.DrawBoxSolid(t, CTrack);
                Text.Font = GameFont.Medium;
                Text.Anchor = TextAnchor.UpperCenter;
                GUI.color = tiles[i].col;
                Widgets.Label(new Rect(t.x, t.y + 4f, t.width, 32f), tiles[i].val);
                Text.Font = GameFont.Tiny;
                GUI.color = CSoft;
                Widgets.Label(new Rect(t.x + 2f, t.y + 36f, t.width - 4f, 20f), tiles[i].cap);
                GUI.color = Color.white;
                Text.Anchor = TextAnchor.UpperLeft;
                TooltipHandler.TipRegion(t, ("HSKTPS.Viewer.TileTip." + i).Translate());
            }
            Text.Font = GameFont.Small;
            return r.yMax;
        }

        float DrawBudget(float x, float y, float w)
        {
            double upd = data.Num("budget_update_ms"), tick = data.Num("budget_tick_ms"), gui = data.Num("budget_gui_ms"), rest = data.Num("budget_rest_ms");
            if (double.IsNaN(upd) || double.IsNaN(gui) || double.IsNaN(rest)) return y;
            if (double.IsNaN(tick)) tick = 0;
            double updOnly = Math.Max(0, upd - tick), total = tick + updOnly + gui + rest;
            if (total <= 0) return y;

            Text.Font = GameFont.Small;
            Widgets.Label(new Rect(x, y, w, 24f), "HSKTPS.Viewer.Budget.Title".Translate(F(total, 1)));
            y += 24f;
            var seg = new[]
            {
                (tick, CTick, "HSKTPS.Viewer.Budget.Ticks".Translate().ToString()),
                (updOnly, CUpdate, "HSKTPS.Viewer.Budget.Update".Translate().ToString()),
                (gui, CGui, "HSKTPS.Viewer.Budget.Gui".Translate().ToString()),
                (rest, CRest, "HSKTPS.Viewer.Budget.Rest".Translate().ToString()),
            };
            var bar = new Rect(x, y, w, 26f);
            float cx = bar.x;
            Text.Font = GameFont.Tiny;
            Text.Anchor = TextAnchor.MiddleCenter;
            foreach (var s in seg)
            {
                float sw = (float)(s.Item1 / total) * bar.width;
                if (sw <= 0f) continue;
                var sr = new Rect(cx, bar.y, sw, bar.height);
                Widgets.DrawBoxSolid(sr, s.Item2);
                string txt = F(s.Item1, 1) + " ms";
                if (Text.CalcSize(txt).x + 6f < sw) { GUI.color = new Color(0.08f, 0.08f, 0.10f); Widgets.Label(sr, txt); GUI.color = Color.white; }
                if (Mouse.IsOver(sr)) hoverText = s.Item3 + ": " + F(s.Item1, 2) + " ms (" + F(s.Item1 / total * 100, 1) + " %)";
                cx += sw;
            }
            Text.Anchor = TextAnchor.UpperLeft;
            y += 30f;
            float lx = x;
            foreach (var s in seg) lx = Legend(lx, y, s.Item2, s.Item3 + " " + F(s.Item1 / total * 100, 0) + "%");
            y += 18f;
            GUI.color = CSoft;
            string hint = "HSKTPS.Viewer.Budget.Hint".Translate();
            float hh = Text.CalcHeight(hint, w);
            Widgets.Label(new Rect(x, y, w, hh), hint);
            GUI.color = Color.white;
            Text.Font = GameFont.Small;
            return y + hh;
        }

        static float Legend(float x, float y, Color c, string label)
        {
            Widgets.DrawBoxSolid(new Rect(x, y + 4f, 10f, 10f), c);
            float lw = Text.CalcSize(label).x;
            GUI.color = CSoft;
            Widgets.Label(new Rect(x + 14f, y, lw + 4f, 18f), label);
            GUI.color = Color.white;
            return x + 14f + lw + 16f;
        }

        // ------------------------------------------------------------------ charts

        /// <summary>Title, y axis with nice gridlines; returns the plot area.</summary>
        Rect ChartFrame(Rect r, string title, double maxY, out double top)
        {
            Text.Font = GameFont.Small;
            Widgets.Label(new Rect(r.x, r.y, r.width, 24f), title);
            var plot = new Rect(r.x + 44f, r.y + 26f, r.width - 48f, r.height - 26f - 16f);
            Widgets.DrawBoxSolid(plot, CTrack);
            double step = Nice(Math.Max(1e-6, maxY) / 4.0);
            top = Math.Max(step, Math.Ceiling(maxY / step) * step);
            Text.Font = GameFont.Tiny;
            Text.Anchor = TextAnchor.MiddleRight;
            GUI.color = CSoft;
            for (double v = 0; v <= top + step * 0.01; v += step)
            {
                float gy = plot.yMax - (float)(v / top) * plot.height;
                Widgets.DrawBoxSolid(new Rect(plot.x, gy, plot.width, 1f), CGrid);
                Widgets.Label(new Rect(r.x, gy - 9f, 40f, 18f), v >= 100 || step >= 1 ? v.ToString("F0", Inv) : v.ToString("0.##", Inv));
            }
            GUI.color = Color.white;
            Text.Anchor = TextAnchor.UpperLeft;
            Text.Font = GameFont.Small;
            return plot;
        }

        static double Nice(double x)
        {
            double p = Math.Pow(10, Math.Floor(Math.Log10(x)));
            double m = x / p;
            return (m <= 1 ? 1 : m <= 2 ? 2 : m <= 5 ? 5 : 10) * p;
        }

        void XAxis(Rect plot, double seconds)
        {
            if (seconds <= 0) return;
            Text.Font = GameFont.Tiny;
            GUI.color = CSoft;
            double step = Nice(seconds / 8.0);
            for (double t = 0; t <= seconds + 1e-6; t += step)
            {
                float x = plot.x + (float)(t / seconds) * plot.width;
                Text.Anchor = TextAnchor.UpperCenter;
                Widgets.Label(new Rect(x - 30f, plot.yMax + 1f, 60f, 16f), t.ToString("0.#", Inv) + " s");
            }
            Text.Anchor = TextAnchor.UpperLeft;
            GUI.color = Color.white;
            Text.Font = GameFont.Small;
        }

        void RefLine(Rect plot, double top, double v, Color c, string label)
        {
            if (v > top) return;
            float gy = plot.yMax - (float)(v / top) * plot.height;
            Widgets.DrawBoxSolid(new Rect(plot.x, gy, plot.width, 1f), new Color(c.r, c.g, c.b, 0.55f));
            Text.Font = GameFont.Tiny;
            Text.Anchor = TextAnchor.LowerRight;
            GUI.color = c;
            Widgets.Label(new Rect(plot.xMax - 160f, gy - 16f, 156f, 16f), label);
            GUI.color = Color.white;
            Text.Anchor = TextAnchor.UpperLeft;
            Text.Font = GameFont.Small;
        }

        /// <summary>
        /// Columns for n values in the plot: one per value while they fit, otherwise one per pixel column showing the value picked by
        /// <paramref name="pick"/> (the worst one, so spikes survive). Returns the hovered value index or -1.
        /// </summary>
        int Columns(Rect plot, int n, Func<int, double> key, Action<int, Rect> drawColumn)
        {
            if (n <= 0) return -1;
            int cols = Math.Min(n, Math.Max(1, (int)plot.width));
            float cw = plot.width / cols;
            float gap = cw >= 5f ? 1f : 0f;
            int hovered = -1;
            bool mouseIn = Mouse.IsOver(plot);
            float mx = Event.current.mousePosition.x;
            for (int c = 0; c < cols; c++)
            {
                int a = (int)((long)c * n / cols), b = Math.Max(a + 1, (int)((long)(c + 1) * n / cols));
                int best = a;
                double bv = key(a);
                for (int i = a + 1; i < b && i < n; i++) { double v = key(i); if (v > bv) { bv = v; best = i; } }
                var col = new Rect(plot.x + c * cw, plot.y, Math.Max(1f, cw - gap), plot.height);
                drawColumn(best, col);
                if (mouseIn && mx >= plot.x + c * cw && mx < plot.x + (c + 1) * cw)
                {
                    hovered = best;
                    Widgets.DrawBoxSolid(new Rect(plot.x + c * cw, plot.y, Math.Max(1f, cw), plot.height), new Color(1f, 1f, 1f, 0.12f));
                }
            }
            return hovered;
        }

        static Rect Bar(Rect col, double top, double from, double to)
        {
            float y0 = col.yMax - (float)(Math.Min(from, top) / top) * col.height;
            float y1 = col.yMax - (float)(Math.Min(to, top) / top) * col.height;
            return new Rect(col.x, y1, col.width, Math.Max(0f, y0 - y1));
        }

        float DrawFrameChart(Rect r)
        {
            var ms = data.FrameMs;
            // scale to the 99.5th percentile so one giant freeze does not flatten everything; taller columns are clipped and marked
            var sorted = (float[])ms.Clone();
            Array.Sort(sorted);
            double cap = Math.Max(20.0, sorted[Math.Min(sorted.Length - 1, (int)(sorted.Length * 0.995))] * 1.1);
            double top;
            var plot = ChartFrame(r, "HSKTPS.Viewer.Chart.Frames".Translate(ms.Length), cap, out top);
            int hov = Columns(plot, ms.Length, i => ms[i], (i, col) =>
            {
                double v = ms[i];
                Color c = v > 100 ? CAwful : v > 50 ? CBad : v > 33.4 ? CWarn : CGui;
                Widgets.DrawBoxSolid(Bar(col, top, 0, v), c);
                if (v > top) Widgets.DrawBoxSolid(new Rect(col.x, col.y, col.width, 3f), Color.white);
            });
            RefLine(plot, top, 16.67, CUpdate, "16.7 ms = 60 FPS");
            RefLine(plot, top, 33.3, CWarn, "33.3 ms = 30 FPS");
            XAxis(plot, data.FrameT.Length > 0 ? data.FrameT[data.FrameT.Length - 1] : 0);
            if (hov >= 0) hoverText = "HSKTPS.Viewer.Hover.Frame".Translate(hov + 1, F(ms[hov], 1), F(data.FrameT.Length > hov ? data.FrameT[hov] : 0, 2));
            return r.yMax;
        }

        float DrawFrameChartFallback(Rect r)
        {
            var tl = data.Timeline;
            double max = tl.Max(s => s.MaxFrame);
            double top;
            var plot = ChartFrame(r, "HSKTPS.Viewer.Chart.FramesFallback".Translate(), Math.Max(20, max * 1.05), out top);
            int hov = Columns(plot, tl.Count, i => tl[i].MaxFrame, (i, col) =>
            {
                Widgets.DrawBoxSolid(Bar(col, top, 0, tl[i].MaxFrame), new Color(CBad.r, CBad.g, CBad.b, 0.45f));
                Widgets.DrawBoxSolid(Bar(col, top, 0, tl[i].AvgFrame), CGui);
            });
            RefLine(plot, top, 16.67, CUpdate, "16.7 ms = 60 FPS");
            RefLine(plot, top, 33.3, CWarn, "33.3 ms = 30 FPS");
            XAxis(plot, tl[tl.Count - 1].T);
            if (hov >= 0) hoverText = SecondText(tl[hov]);
            return r.yMax;
        }

        float DrawTpsChart(Rect r)
        {
            var tl = data.Timeline;
            double max = Math.Max(60, tl.Max(s => (double)s.Ticks));
            double top;
            var plot = ChartFrame(r, "HSKTPS.Viewer.Chart.Tps".Translate(), max * 1.05, out top);
            int hov = Columns(plot, tl.Count, i => -tl[i].Ticks /* the slowest second wins when squeezed */, (i, col) =>
                Widgets.DrawBoxSolid(Bar(col, top, 0, tl[i].Ticks), CTick));
            RefLine(plot, top, 60, CSoft, "60 TPS = 1x");
            XAxis(plot, tl[tl.Count - 1].T);
            if (hov >= 0) hoverText = SecondText(tl[hov]);
            return r.yMax;
        }

        float DrawLoadChart(Rect r)
        {
            var tl = data.Timeline;
            double max = tl.Max(s => Math.Max(s.UpdateMs, s.TickMs) + s.GuiMs);
            double top;
            var plot = ChartFrame(r, "HSKTPS.Viewer.Chart.Load".Translate(), Math.Max(100, max * 1.05), out top);
            int hov = Columns(plot, tl.Count, i => tl[i].UpdateMs + tl[i].GuiMs, (i, col) =>
            {
                var s = tl[i];
                double tick = s.TickMs, upd = Math.Max(0, s.UpdateMs - s.TickMs);
                Widgets.DrawBoxSolid(Bar(col, top, 0, tick), CTick);
                Widgets.DrawBoxSolid(Bar(col, top, tick, tick + upd), CUpdate);
                Widgets.DrawBoxSolid(Bar(col, top, tick + upd, tick + upd + s.GuiMs), CGui);
                if (s.Gc2 > 0) Widgets.DrawBoxSolid(new Rect(col.x, col.y, col.width, 4f), CAwful);
                else if (s.Gc1 > 0) Widgets.DrawBoxSolid(new Rect(col.x, col.y, col.width, 3f), CWarn);
            });
            RefLine(plot, top, 1000, CSoft, "1000 ms = " + "HSKTPS.Viewer.WholeSecond".Translate());
            XAxis(plot, tl[tl.Count - 1].T);
            float ly = r.yMax + 2f;
            Text.Font = GameFont.Tiny;
            float lx = plot.x;
            lx = Legend(lx, ly, CTick, "HSKTPS.Viewer.Budget.Ticks".Translate());
            lx = Legend(lx, ly, CUpdate, "HSKTPS.Viewer.Budget.Update".Translate());
            lx = Legend(lx, ly, CGui, "HSKTPS.Viewer.Budget.Gui".Translate());
            lx = Legend(lx, ly, CAwful, "HSKTPS.Viewer.Gc2".Translate());
            Text.Font = GameFont.Small;
            if (hov >= 0) hoverText = SecondText(tl[hov]);
            return r.yMax + 20f;
        }

        string SecondText(ReportData.Second s)
        {
            return "HSKTPS.Viewer.Hover.Second".Translate(F(s.T, 0), s.Ticks, s.Frames, F(s.AvgFrame, 1), F(s.MaxFrame, 1)) + "\n"
                + "HSKTPS.Viewer.Hover.SecondLoad".Translate(F(s.TickMs, 0), F(Math.Max(0, s.UpdateMs - s.TickMs), 0), F(s.GuiMs, 0)) + "\n"
                + "HSKTPS.Viewer.Hover.SecondGc".Translate(s.Gc0 + "/" + s.Gc1 + "/" + s.Gc2, F(s.HeapMb, 0), s.Speed ?? "");
        }

        // ------------------------------------------------------------------ mods

        void DrawMods(Rect r)
        {
            var rows = data.Mods.Where(m => m.Total > 0.0005 || m.Content > 0.0005).ToList();
            float y = r.y;
            Text.Font = GameFont.Tiny;
            float lx = r.x;
            lx = Legend(lx, y, CTick, "HSKTPS.Viewer.Cat.Tick".Translate());
            lx = Legend(lx, y, CUpdate, "HSKTPS.Viewer.Cat.Update".Translate());
            lx = Legend(lx, y, CGui, "HSKTPS.Viewer.Cat.GUI".Translate());
            lx = Legend(lx, y, CHarmony, "HSKTPS.Viewer.Cat.Harmony".Translate());
            y += 20f;
            GUI.color = CSoft;
            string hint = "HSKTPS.Viewer.Mods.Hint".Translate();
            float hh = Text.CalcHeight(hint, r.width);
            Widgets.Label(new Rect(r.x, y, r.width, hh), hint);
            GUI.color = Color.white;
            y += hh + 4f;

            const float rowH = 24f, nameW = 270f, numW = 74f;
            // header
            float barW = r.width - 16f - nameW - numW * 3 - 12f;
            Text.Anchor = TextAnchor.MiddleRight;
            GUI.color = CSoft;
            float hx = r.x + nameW + barW + 8f;
            Widgets.Label(new Rect(hx, y, numW, 20f), "HSKTPS.Viewer.Col.MsS".Translate());
            Widgets.Label(new Rect(hx + numW, y, numW, 20f), "HSKTPS.Viewer.Col.Content".Translate());
            Widgets.Label(new Rect(hx + numW * 2, y, numW, 20f), "HSKTPS.Viewer.Col.Worst".Translate());
            GUI.color = Color.white;
            Text.Anchor = TextAnchor.UpperLeft;
            y += 20f;

            var outR = new Rect(r.x, y, r.width, r.yMax - y);
            var view = new Rect(0f, 0f, r.width - 16f, Math.Max(outR.height, rows.Count * rowH));
            Widgets.BeginScrollView(outR, ref modsScroll, view);
            double max = rows.Count > 0 ? rows.Max(m => m.Total) : 1;
            if (max <= 0) max = 1;
            int first = Math.Max(0, (int)(modsScroll.y / rowH)), last = Math.Min(rows.Count, first + (int)(outR.height / rowH) + 2);
            for (int i = first; i < last; i++)
            {
                var m = rows[i];
                var row = new Rect(0f, i * rowH, view.width, rowH);
                if (i % 2 == 1) Widgets.DrawBoxSolid(row, new Color(1f, 1f, 1f, 0.025f));
                Widgets.DrawHighlightIfMouseover(row);
                Text.Anchor = TextAnchor.MiddleLeft;
                Widgets.Label(new Rect(row.x + 4f, row.y, nameW - 8f, rowH), m.Mod.Truncate(nameW - 8f));
                var bar = new Rect(row.x + nameW, row.y + 5f, barW, rowH - 10f);
                Widgets.DrawBoxSolid(bar, CTrack);
                float bx = bar.x;
                foreach (var part in new[] { (m.Tick, CTick), (m.Update, CUpdate), (m.Gui, CGui), (m.Harmony, CHarmony) })
                {
                    float pw = (float)(part.Item1 / max) * bar.width;
                    if (pw <= 0f) continue;
                    Widgets.DrawBoxSolid(new Rect(bx, bar.y, pw, bar.height), part.Item2);
                    bx += pw;
                }
                Text.Anchor = TextAnchor.MiddleRight;
                float nx = bar.xMax + 8f;
                Widgets.Label(new Rect(nx, row.y, numW, rowH), F(m.Total, 2));
                GUI.color = CSoft;
                Widgets.Label(new Rect(nx + numW, row.y, numW, rowH), m.Content > 0 ? F(m.Content, 2) : "");
                Widgets.Label(new Rect(nx + numW * 2, row.y, numW, rowH), F(m.Worst, 2));
                GUI.color = Color.white;
                Text.Anchor = TextAnchor.UpperLeft;
                if (Mouse.IsOver(row))
                {
                    TooltipHandler.TipRegion(row, "HSKTPS.Viewer.Mods.Tip".Translate(m.Mod, F(m.Total, 2), F(m.Tick, 2), F(m.Update, 2), F(m.Gui, 2), F(m.Harmony, 2), F(m.CallsS, 0), F(m.Worst, 2), F(m.Content, 2), m.TopHook ?? ""));
                    if (Widgets.ButtonInvisible(row))
                    {
                        methodsTree.SetQuery(m.Mod);
                        tab = Tab.Methods;
                    }
                }
            }
            Widgets.EndScrollView();
            Text.Font = GameFont.Small;
        }

        // ------------------------------------------------------------------ hover box

        void DrawHoverBox(string text)
        {
            Text.Font = GameFont.Tiny;
            var size = Text.CalcSize(text);
            float w = Mathf.Min(size.x + 16f, 520f), h = Text.CalcHeight(text, w - 12f) + 10f;
            var mp = Event.current.mousePosition;
            var r = new Rect(mp.x + 18f, mp.y + 18f, w, h);
            if (r.xMax > windowRect.width - 20f) r.x = mp.x - w - 10f;
            if (r.yMax > windowRect.height - 20f) r.y = mp.y - h - 10f;
            Widgets.DrawBoxSolid(r, new Color(0.08f, 0.09f, 0.11f, 0.95f));
            Widgets.DrawBox(r);
            Widgets.Label(r.ContractedBy(6f, 5f), text);
            Text.Font = GameFont.Small;
        }

        /// <summary>Lets the trees hand a hover line to the window (it is drawn last, on top of everything).</summary>
        internal void SetHover(string s) { hoverText = s; }

        // ------------------------------------------------------------------ tree (methods / defs)

        internal sealed class TreeView
        {
            sealed class Node
            {
                public string Key, Label, Cat;
                public int Level;
                public ReportData.Row Leaf;
                public double Ms, CallsS, Worst;
                public int Count;
                public List<Node> Kids;
            }

            readonly bool methods;
            List<ReportData.Row> rows = new List<ReportData.Row>();
            readonly HashSet<string> open = new HashSet<string>();
            readonly List<Node> visible = new List<Node>();
            readonly double[] levelMax = new double[3];
            string query = "", builtQuery;
            int sortCol;            // 0 ms/s, 1 calls/s, 2 us/call, 3 worst
            bool dirty = true;
            Vector2 scroll;
            double total;

            public TreeView(bool methods) { this.methods = methods; }

            public void SetRows(List<ReportData.Row> r) { rows = r; open.Clear(); dirty = true; scroll = Vector2.zero; }
            public void SetQuery(string q) { query = q ?? ""; dirty = true; scroll = Vector2.zero; }

            double SortKey(Node n)
            {
                switch (sortCol)
                {
                    case 1: return n.CallsS;
                    case 2: return n.Leaf != null ? n.Leaf.Us : (n.CallsS > 0 ? n.Ms * 1000.0 / n.CallsS : 0);
                    case 3: return n.Worst;
                    default: return n.Ms;
                }
            }

            void Rebuild()
            {
                dirty = false;
                builtQuery = query;
                string q = query.Trim();
                bool filtering = q.Length > 0;
                var roots = new List<Node>();
                var byCat = new Dictionary<string, Node>();
                var byMod = new Dictionary<string, Node>();
                foreach (var r in rows)
                {
                    if (filtering && !Matches(r, q)) continue;
                    Node c;
                    if (!byCat.TryGetValue(r.Cat, out c))
                    {
                        c = new Node { Key = r.Cat, Label = methods ? CatLabel(r.Cat) : r.Cat, Cat = r.Cat, Level = 0, Kids = new List<Node>() };
                        byCat[r.Cat] = c; roots.Add(c);
                    }
                    string mk = r.Cat + "\u0001" + r.Mod;
                    Node m;
                    if (!byMod.TryGetValue(mk, out m))
                    {
                        m = new Node { Key = mk, Label = r.Mod, Cat = r.Cat, Level = 1, Kids = new List<Node>() };
                        byMod[mk] = m; c.Kids.Add(m);
                    }
                    var leaf = new Node { Key = mk + "\u0001" + r.Name + "\u0001" + r.Extra, Label = r.Name, Cat = r.Cat, Level = 2, Leaf = r, Ms = r.Ms, CallsS = r.CallsS, Worst = r.Worst, Count = 1 };
                    m.Kids.Add(leaf);
                    foreach (var n in new[] { m, c }) { n.Ms += r.Ms; n.CallsS += r.CallsS; n.Worst = Math.Max(n.Worst, r.Worst); n.Count++; }
                }
                Comparison<Node> cmp = (a, b) => SortKey(b).CompareTo(SortKey(a));
                roots.Sort(cmp);
                foreach (var c in roots) { c.Kids.Sort(cmp); foreach (var m in c.Kids) m.Kids.Sort(cmp); }
                total = roots.Sum(n => n.Ms);
                for (int i = 0; i < 3; i++) levelMax[i] = 1e-9;
                visible.Clear();
                foreach (var c in roots) Walk(c, filtering);
            }

            void Walk(Node n, bool forceOpen)
            {
                visible.Add(n);
                if (n.Ms > levelMax[n.Level]) levelMax[n.Level] = n.Ms;
                if (n.Kids == null || !(forceOpen || open.Contains(n.Key))) return;
                foreach (var k in n.Kids) Walk(k, forceOpen);
            }

            static bool Matches(ReportData.Row r, string q)
            {
                return Has(r.Name, q) || Has(r.Mod, q) || Has(r.Extra, q) || Has(r.Family, q) || Has(r.Type, q);
            }

            static bool Has(string s, string q) { return s != null && s.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0; }

            public void Draw(Rect r, ReportData data, Dialog_ReportViewer owner)
            {
                // search + expand/collapse
                float y = r.y;
                string q = Widgets.TextField(new Rect(r.x, y, Math.Min(360f, r.width * 0.45f), 28f), query);
                if (q != query) { query = q; dirty = true; scroll = Vector2.zero; }
                if (query.Length == 0)
                {
                    Text.Font = GameFont.Tiny;
                    GUI.color = CSoft;
                    Text.Anchor = TextAnchor.MiddleLeft;
                    Widgets.Label(new Rect(r.x + 8f, y, 300f, 28f), "HSKTPS.Viewer.Search".Translate());
                    Text.Anchor = TextAnchor.UpperLeft;
                    GUI.color = Color.white;
                    Text.Font = GameFont.Small;
                }
                float bx = r.x + Math.Min(360f, r.width * 0.45f) + 8f;
                if (Widgets.ButtonText(new Rect(bx, y, 120f, 28f), "HSKTPS.Viewer.ExpandAll".Translate()))
                {
                    foreach (var row in rows) { open.Add(row.Cat); open.Add(row.Cat + "\u0001" + row.Mod); }
                    dirty = true;
                }
                if (Widgets.ButtonText(new Rect(bx + 126f, y, 120f, 28f), "HSKTPS.Viewer.CollapseAll".Translate())) { open.Clear(); dirty = true; }
                if (query.Length > 0 && Widgets.ButtonText(new Rect(bx + 252f, y, 90f, 28f), "HSKTPS.Viewer.ClearSearch".Translate())) { query = ""; dirty = true; }
                y += 32f;

                if (dirty || builtQuery != query) Rebuild();

                Text.Font = GameFont.Tiny;
                GUI.color = CSoft;
                string hint = (methods ? "HSKTPS.Viewer.Methods.Hint" : "HSKTPS.Viewer.Defs.Hint").Translate(F(total, 1));
                float hh = Text.CalcHeight(hint, r.width);
                Widgets.Label(new Rect(r.x, y, r.width, hh), hint);
                GUI.color = Color.white;
                y += hh + 4f;

                // columns
                const float rowH = 22f, barW = 130f, numW = 72f;
                float nameW = r.width - 16f - barW - numW * 5 - 10f;
                string[] heads = { "HSKTPS.Viewer.Col.MsS", "HSKTPS.Viewer.Col.Pct", "HSKTPS.Viewer.Col.CallsS", "HSKTPS.Viewer.Col.UsCall", "HSKTPS.Viewer.Col.Worst" };
                int[] sortOf = { 0, 0, 1, 2, 3 };
                float hx = r.x + nameW + barW + 10f;
                Text.Anchor = TextAnchor.MiddleRight;
                for (int i = 0; i < heads.Length; i++)
                {
                    var hr = new Rect(hx + i * numW, y, numW, 20f);
                    bool active = sortCol == sortOf[i] && i != 1;
                    GUI.color = active ? Color.white : CSoft;
                    Widgets.Label(hr, heads[i].Translate() + (active ? " ▾" : ""));
                    if (i != 1 && Widgets.ButtonInvisible(hr)) { sortCol = sortOf[i]; dirty = true; }
                }
                GUI.color = Color.white;
                Text.Anchor = TextAnchor.UpperLeft;
                y += 22f;

                var outR = new Rect(r.x, y, r.width, r.yMax - y);
                if (visible.Count == 0)
                {
                    Widgets.Label(outR, (rows.Count == 0 ? "HSKTPS.Viewer.Empty" : "HSKTPS.Viewer.NothingFound").Translate());
                    Text.Font = GameFont.Small;
                    return;
                }
                var view = new Rect(0f, 0f, r.width - 16f, Math.Max(outR.height, visible.Count * rowH));
                Widgets.BeginScrollView(outR, ref scroll, view);
                int first = Math.Max(0, (int)(scroll.y / rowH)), last = Math.Min(visible.Count, first + (int)(outR.height / rowH) + 2);
                bool toggled = false;
                for (int i = first; i < last; i++)
                {
                    var n = visible[i];
                    var row = new Rect(0f, i * rowH, view.width, rowH);
                    if (n.Level == 0) Widgets.DrawBoxSolid(row, new Color(1f, 1f, 1f, 0.05f));
                    Widgets.DrawHighlightIfMouseover(row);
                    float indent = 4f + n.Level * 18f;
                    Text.Anchor = TextAnchor.MiddleLeft;
                    Text.Font = n.Level == 0 ? GameFont.Small : GameFont.Tiny;
                    if (n.Kids != null)
                    {
                        bool isOpen = query.Trim().Length > 0 || open.Contains(n.Key);
                        GUI.color = CSoft;
                        Widgets.Label(new Rect(indent, row.y, 16f, rowH), isOpen ? "▾" : "▸");
                        GUI.color = Color.white;
                    }
                    if (n.Level == 0) Widgets.DrawBoxSolid(new Rect(0f, row.y + 2f, 3f, rowH - 4f), methods ? CatColor(n.Cat) : CDef);
                    string label = n.Label + (n.Kids != null ? "  (" + n.Count + ")" : "");
                    GUI.color = n.Level == 2 ? new Color(0.88f, 0.89f, 0.91f) : Color.white;
                    Widgets.Label(new Rect(indent + 16f, row.y, nameW - indent - 16f, rowH), label.Truncate(nameW - indent - 18f));
                    GUI.color = Color.white;

                    var bar = new Rect(nameW, row.y + 5f, barW, rowH - 10f);
                    Widgets.DrawBoxSolid(bar, CTrack);
                    float fill = (float)Math.Min(1.0, n.Ms / levelMax[n.Level]);
                    Widgets.DrawBoxSolid(new Rect(bar.x, bar.y, bar.width * fill, bar.height), methods ? CatColor(n.Cat) : CDef);

                    Text.Font = GameFont.Tiny;
                    Text.Anchor = TextAnchor.MiddleRight;
                    float nx = bar.xMax + 10f;
                    Widgets.Label(new Rect(nx, row.y, numW, rowH), F(n.Ms, n.Ms >= 10 ? 1 : 2));
                    GUI.color = CSoft;
                    Widgets.Label(new Rect(nx + numW, row.y, numW, rowH), total > 0 ? F(n.Ms / total * 100, 1) + "%" : "");
                    Widgets.Label(new Rect(nx + numW * 2, row.y, numW, rowH), F(n.CallsS, 0));
                    Widgets.Label(new Rect(nx + numW * 3, row.y, numW, rowH), n.Leaf != null ? F(n.Leaf.Us, 2) : "");
                    Widgets.Label(new Rect(nx + numW * 4, row.y, numW, rowH), F(n.Worst, 2));
                    GUI.color = Color.white;
                    Text.Anchor = TextAnchor.UpperLeft;

                    if (Mouse.IsOver(row))
                    {
                        if (n.Leaf != null) TooltipHandler.TipRegion(row, LeafTip(n.Leaf));
                        if (Widgets.ButtonInvisible(row))
                        {
                            if (n.Kids != null)
                            {
                                if (query.Trim().Length == 0) { if (!open.Remove(n.Key)) open.Add(n.Key); toggled = true; }
                            }
                            else
                            {
                                GUIUtility.systemCopyBuffer = n.Leaf.Name;
                                Verse.Sound.SoundStarter.PlayOneShotOnCamera(SoundDefOf.Click);
                            }
                        }
                    }
                }
                Widgets.EndScrollView();
                Text.Font = GameFont.Small;
                if (toggled) dirty = true;
            }

            string LeafTip(ReportData.Row r)
            {
                var s = r.Name;
                if (!string.IsNullOrEmpty(r.Type)) s += "\n" + r.Type;
                if (!string.IsNullOrEmpty(r.Extra)) s += "\n" + r.Extra;
                s += "\n\n" + "HSKTPS.Viewer.LeafTip".Translate(r.Mod, string.IsNullOrEmpty(r.Family) ? "—" : r.Family, F(r.Ms, 3), F(r.CallsS, 0), F(r.Us, 3), F(r.Worst, 3));
                return s;
            }
        }
    }
}
