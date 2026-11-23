using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace HSKPerformance
{
    /// <summary>
    /// One profiler dump read back from disk (report-*/ folder), for the in-game viewer.
    /// Reads the CSV files the recorder writes; headline numbers come from summary.txt, or from report.md for dumps made before it existed.
    /// </summary>
    public sealed class ReportData
    {
        public sealed class Row          // one method (methods.csv) or one def (defs.csv)
        {
            public string Cat, Mod, Name, Family, Extra, Type;
            public double Ms, CallsS, Us, Worst;
        }

        public sealed class ModRow
        {
            public string Mod, TopHook;
            public double Total, Tick, Update, Gui, Harmony, CallsS, Worst, Content;
        }

        public sealed class Second
        {
            public double T, AvgFrame, MaxFrame, TickMs, UpdateMs, GuiMs, HeapMb;
            public int Ticks, Frames, Gc0, Gc1, Gc2;
            public string Speed;
        }

        public string Dir, Error;
        public readonly Dictionary<string, string> Summary = new Dictionary<string, string>();
        public readonly List<string> Context = new List<string>();
        public readonly List<Row> Methods = new List<Row>();
        public readonly List<Row> Defs = new List<Row>();
        public readonly List<ModRow> Mods = new List<ModRow>();
        public readonly List<Second> Timeline = new List<Second>();
        public float[] FrameMs = new float[0];
        public float[] FrameT = new float[0];

        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public string Str(string k) { string v; return Summary.TryGetValue(k, out v) ? v : null; }
        public double Num(string k) { double d; return double.TryParse(Str(k), NumberStyles.Float, Inv, out d) ? d : double.NaN; }

        // ------------------------------------------------------------------ list

        /// <summary>report-* folders, newest first (the folder name starts with the timestamp, so a name sort is a time sort).</summary>
        public static List<string> ListReports()
        {
            var l = new List<string>();
            try
            {
                if (!Directory.Exists(ProbeConfig.Dir)) return l;
                foreach (var d in Directory.GetDirectories(ProbeConfig.Dir, "report-*"))
                    // light recordings (mod hooks off) are gone from the recorder; older ones on disk are not worth showing
                    if (File.Exists(Path.Combine(d, "methods.csv")) && !d.EndsWith("-light", StringComparison.Ordinal)) l.Add(d);
                l.Sort((a, b) => string.CompareOrdinal(Path.GetFileName(b), Path.GetFileName(a)));
            }
            catch { }
            return l;
        }

        /// <summary>"2026-10-21 15:09:35" from a report-20261021-150935 folder name (older ones end in -full).</summary>
        public static string Label(string dir)
        {
            string n = Path.GetFileName(dir) ?? "";
            var m = Regex.Match(n, @"^report-(\d{4})(\d{2})(\d{2})-(\d{2})(\d{2})(\d{2})(?:-(\w+))?$");
            if (!m.Success) return n;
            string s = m.Groups[1].Value + "-" + m.Groups[2].Value + "-" + m.Groups[3].Value + " " + m.Groups[4].Value + ":" + m.Groups[5].Value + ":" + m.Groups[6].Value;
            return s;
        }

        // ------------------------------------------------------------------ load

        public static ReportData Load(string dir)
        {
            var r = new ReportData { Dir = dir };
            try
            {
                r.ReadSummary();
                foreach (var f in ReadCsv(Path.Combine(dir, "methods.csv")))
                {
                    string cat = f.S("category");
                    if (cat == "Calibration") continue;
                    r.Methods.Add(new Row
                    {
                        Cat = cat, Mod = f.S("mod"), Name = f.S("method"), Family = f.S("family"), Extra = f.S("extra"), Type = f.S("type"),
                        Ms = f.D("excl_ms_s"), CallsS = f.D("calls_s"), Us = f.D("avg_excl_us_per_call"), Worst = f.D("worst_excl_ms"),
                    });
                }
                foreach (var f in ReadCsv(Path.Combine(dir, "defs.csv")))
                    r.Defs.Add(new Row
                    {
                        Cat = f.S("kind"), Mod = f.S("from_mod"), Name = f.S("def_name"), Family = "", Extra = "", Type = "",
                        Ms = f.D("ms_s"), CallsS = f.D("calls_s"), Us = f.D("avg_us_per_call"), Worst = f.D("worst_ms"),
                    });
                foreach (var f in ReadCsv(Path.Combine(dir, "mods.csv")))
                    r.Mods.Add(new ModRow
                    {
                        Mod = f.S("mod"), TopHook = f.S("biggest_hook"),
                        Total = f.D("total_ms_s"), Tick = f.D("tick_ms_s"), Update = f.D("update_ms_s"), Gui = f.D("gui_ms_s"), Harmony = f.D("harmony_ms_s"),
                        CallsS = f.D("calls_s"), Worst = f.D("worst_call_ms"), Content = f.D("content_ms_s"),
                    });
                foreach (var f in ReadCsv(Path.Combine(dir, "timeline.csv")))
                    r.Timeline.Add(new Second
                    {
                        T = f.D("t_s"), Ticks = (int)f.D("ticks"), Frames = (int)f.D("frames"), AvgFrame = f.D("avg_frame_ms"), MaxFrame = f.D("max_frame_ms"),
                        TickMs = f.D("tick_ms"), UpdateMs = f.D("update_ms"), GuiMs = f.D("gui_ms"),
                        Gc0 = (int)f.D("gc0"), Gc1 = (int)f.D("gc1"), Gc2 = (int)f.D("gc2"), HeapMb = f.D("heap_mb"), Speed = f.S("speed"),
                    });
                var ft = new List<float>(); var fm = new List<float>();
                foreach (var f in ReadCsv(Path.Combine(dir, "frames.csv"))) { ft.Add((float)f.D("t_s")); fm.Add((float)f.D("frame_ms")); }
                r.FrameT = ft.ToArray(); r.FrameMs = fm.ToArray();
            }
            catch (Exception e)
            {
                r.Error = e.GetType().Name + ": " + e.Message;
            }
            return r;
        }

        void ReadSummary()
        {
            string p = Path.Combine(Dir, "summary.txt");
            if (File.Exists(p))
            {
                foreach (var line in File.ReadAllLines(p, Encoding.UTF8))
                {
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string k = line.Substring(0, eq), v = line.Substring(eq + 1);
                    if (k == "context") Context.Add(v); else Summary[k] = v;
                }
                return;
            }
            // older dumps: pick the headline numbers out of report.md
            p = Path.Combine(Dir, "report.md");
            if (!File.Exists(p)) return;
            string t = File.ReadAllText(p, Encoding.UTF8);
            Match m;
            if ((m = Regex.Match(t, @"Recorded ([0-9: \-]+), stopped by: ([^\n]*?)\.\s*\n")).Success) { Summary["recorded"] = m.Groups[1].Value.Trim(); Summary["stop_reason"] = m.Groups[2].Value; }
            if ((m = Regex.Match(t, @"Capture: \*\*([0-9.]+) s\*\*.*?, ([0-9]+) frames, ([0-9]+) ticks")).Success) { Summary["capture_s"] = m.Groups[1].Value; Summary["frames"] = m.Groups[2].Value; Summary["ticks"] = m.Groups[3].Value; }
            if ((m = Regex.Match(t, @"FPS avg ([0-9.]+)\*\*, \*\*TPS avg ([0-9.]+)")).Success) { Summary["fps"] = m.Groups[1].Value; Summary["tps"] = m.Groups[2].Value; }
            if ((m = Regex.Match(t, @"probe overhead removed\*\*: \*\*([0-9.]+) ms/s")).Success) Summary["measured_ms_s"] = m.Groups[1].Value;
            if ((m = Regex.Match(t, @"so \*\*([0-9.]+) ms/s\*\*")).Success) Summary["overhead_ms_s"] = m.Groups[1].Value;
            if ((m = Regex.Match(t, @"Mode: \*\*(LIGHT|FULL)\*\*")).Success) Summary["mode"] = m.Groups[1].Value.ToLowerInvariant();
            if ((m = Regex.Match(t, @"Per tick: average \*\*([0-9.]+) ms\*\*, worst single tick ([0-9.]+) ms")).Success) { Summary["tick_avg_ms"] = m.Groups[1].Value; Summary["tick_worst_ms"] = m.Groups[2].Value; }
            if ((m = Regex.Match(t, @"\| whole frame \(measured by Unity\) \| ([0-9.]+) \|")).Success) Summary["frame_avg_ms"] = m.Groups[1].Value;
            if ((m = Regex.Match(t, @"\| Root_Play\.Update, everything in it \| ([0-9.]+) \|")).Success) Summary["budget_update_ms"] = m.Groups[1].Value;
            if ((m = Regex.Match(t, @"of which ticks \(DoSingleTick\) \| ([0-9.]+) \|")).Success) Summary["budget_tick_ms"] = m.Groups[1].Value;
            if ((m = Regex.Match(t, @"\| UIRoot_Play\.UIRootOnGUI, everything in it \| ([0-9.]+) \|")).Success) Summary["budget_gui_ms"] = m.Groups[1].Value;
            if ((m = Regex.Match(t, @"\| rest: rendering[^|]*\| ([0-9.]+) \|")).Success) Summary["budget_rest_ms"] = m.Groups[1].Value;
            // | avg | p50 | p90 | p95 | p99 | max | >33 | >50 | >100 |
            if ((m = Regex.Match(t, @"\|---:\|---:\|---:\|---:\|---:\|---:\|---:\|---:\|---:\|\s*\n\| ([0-9.]+) \| ([0-9.]+) \| ([0-9.]+) \| ([0-9.]+) \| ([0-9.]+) \| ([0-9.]+) \| ([0-9]+) \| ([0-9]+) \| ([0-9]+) \|")).Success)
            {
                string[] keys = { "frame_avg_ms", "frame_p50_ms", "frame_p90_ms", "frame_p95_ms", "frame_p99_ms", "frame_max_ms", "frames_over_33", "frames_over_50", "frames_over_100" };
                for (int i = 0; i < keys.Length; i++) Summary[keys[i]] = m.Groups[i + 1].Value;
            }
            if ((m = Regex.Match(t, @"Start state:\s*\n((?:- [^\n]*\n)+)")).Success)
                foreach (var line in m.Groups[1].Value.Split('\n'))
                    if (line.StartsWith("- ")) Context.Add(line.Substring(2));
        }

        // ------------------------------------------------------------------ csv

        sealed class CsvRec
        {
            public Dictionary<string, int> Head;
            public List<string> Cells;
            public string S(string k) { int i; return Head.TryGetValue(k, out i) && i < Cells.Count ? Cells[i].Trim() : ""; }
            public double D(string k) { double d; return double.TryParse(S(k), NumberStyles.Float, Inv, out d) ? d : 0; }
        }

        static IEnumerable<CsvRec> ReadCsv(string path)
        {
            if (!File.Exists(path)) yield break;
            string text = File.ReadAllText(path, Encoding.UTF8);
            Dictionary<string, int> head = null;
            int pos = 0;
            while (pos < text.Length)
            {
                var cells = ParseLine(text, ref pos);
                if (cells.Count == 1 && cells[0].Length == 0) continue;
                if (head == null)
                {
                    head = new Dictionary<string, int>();
                    for (int i = 0; i < cells.Count; i++) head[cells[i].Trim()] = i;
                    continue;
                }
                yield return new CsvRec { Head = head, Cells = cells };
            }
        }

        /// <summary>One RFC 4180 record (quoted cells may hold commas, quotes and line breaks).</summary>
        static List<string> ParseLine(string s, ref int pos)
        {
            var cells = new List<string>();
            var sb = new StringBuilder();
            bool quoted = false;
            while (pos < s.Length)
            {
                char c = s[pos++];
                if (quoted)
                {
                    if (c == '"')
                    {
                        if (pos < s.Length && s[pos] == '"') { sb.Append('"'); pos++; }
                        else quoted = false;
                    }
                    else sb.Append(c);
                }
                else if (c == '"') quoted = true;
                else if (c == ',') { cells.Add(sb.ToString()); sb.Length = 0; }
                else if (c == '\n') break;
                else if (c != '\r') sb.Append(c);
            }
            cells.Add(sb.ToString());
            return cells;
        }
    }
}
