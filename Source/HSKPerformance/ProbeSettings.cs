using RimWorld;
using UnityEngine;
using Verse;

namespace HSKPerformance
{
    /// <summary>
    /// Options > Mod settings > HSK TPS Booster: four switches for the fixes and one slider for the far map.
    /// Everything else (recording length, hook groups, colours, speed thresholds) lives in config.txt, which the recorder and the fixes read.
    /// </summary>
    public sealed class HSKPerformanceMod : Mod
    {
        ProbeConfig cfg;
        Vector2 scroll;
        bool sRot, sMap, sEff, sMoth, sBadge, sCam, sFps; // what was last applied, to notice a click

        public HSKPerformanceMod(ModContentPack content) : base(content) { }

        public override string SettingsCategory() { return "HSK TPS Booster"; }

        void Ensure()
        {
            if (cfg != null) return;
            cfg = ProbeConfig.Load();
            sRot = cfg.FixRotStorage; sMap = cfg.FixFarMap; sEff = cfg.FixPawnEffects; sMoth = cfg.FixMothballHediffs; sBadge = cfg.MarkerBadges; sCam = cfg.FixCameraCache; sFps = cfg.ShowFpsTps;
        }

        public override void DoSettingsWindowContents(Rect inRect)
        {
            Ensure();
            var view = new Rect(0f, 0f, inRect.width - 20f, 460f);
            Widgets.BeginScrollView(inRect, ref scroll, view);
            var l = new Listing_Standard();
            l.Begin(view);

            l.Label("Исправления (действуют сразу, перезапуск не нужен)");
            l.Gap(4f);
            l.CheckboxLabeled("Быстрый поиск склада для гниения", ref cfg.FixRotStorage,
                "HSK Core искал склад для каждой гниющей вещи перебором всех хранилищ. Теперь это одна проверка клетки.");
            l.CheckboxLabeled("Без косметических эффектов на высокой скорости", ref cfg.FixPawnEffects,
                "Со второй скорости и выше не создаются следы пешек, пар от дыхания, рябь на воде и дым разбитых капсул и кораблей. Дым печей, огонь костров и пожары остаются. На игру не влияет, только на вид.");
            l.CheckboxLabeled("Заморозка мировых пешек с хроническими болезнями", ref cfg.FixMothballHediffs,
                "Такие пешки перестают тикать каждый тик. Побочный эффект: эти болезни больше не убивают мировых пешек.");
            l.CheckboxLabeled("Интерактивная карта", ref cfg.FixFarMap,
                "При большом отдалении вместо обычной карты рисуются стены, горы и глубокая вода на одном фоне. Пешки, животные и враги остаются. Чертежи и недостроенные каркасы показаны ярко-синими клетками (терраформинг и полы не показываются). Свежие трупы животных вне складов - розовые точки, летящие пули и снаряды - жёлтые, торнадо, смерчи и ледяные вихри показаны целиком. Дождь, снег и молнии не рисуются, пока карта активна (сама погода продолжает идти)."
                + "\n\nМаркеры Camera+ закрашены по статусу: колонисты зелёные (выбранные, лежачие и в срыве остаются как в Camera+), гости светло-голубые, пленные оранжевые, рабы жёлтые, хищники не из вашей колонии красные. Под маркером выбранной пешки рисуется её имя, слева сверху от маркера колониста - его первый значок из мода Pawn Badge (если тот включён). При наведении курсора маркер не заменяется реальной пешкой - показывается только имя. Сильно снижает нагрузку при отдалении.");
            if (cfg.FixFarMap)
            {
                l.Gap(2f);
                l.Label("Включать, когда клетка меньше " + cfg.FarMapPx.ToString("F0") + " px (сейчас " + FarMap.CurrentPx.ToString("F0") + " px). Больше число: включается на более близком зуме.");
                cfg.FarMapPx = l.Slider(cfg.FarMapPx, 4f, 60f);
                FarMap.ThresholdPx = cfg.FarMapPx;
                l.Indent(20f);
                l.ColumnWidth -= 20f;
                l.CheckboxLabeled("Значки Pawn Badge на маркерах колонистов", ref cfg.MarkerBadges,
                    "Первый значок колониста из мода Pawn Badge рисуется слева сверху от его маркера Camera+. Ничего не делает, если мод Pawn Badge не загружен.");
                l.CheckboxLabeled("Кэш Camera+ (зум и цвета маркеров)", ref cfg.FixCameraCache,
                    "Camera+ пересчитывает зум камеры и цвета маркеров тысячи раз за кадр. С кэшем это считается один раз за кадр, остальное берётся из памяти (~26 мс/с процессора). Выключать имеет смысл только для сравнения замеров.");
                l.ColumnWidth += 20f;
                l.Outdent(20f);
            }

            l.CheckboxLabeled("Счётчик FPS / TPS в углу экрана", ref cfg.ShowFpsTps,
                "Небольшой счётчик кадров и тиков в правом верхнем углу (как в Dubs Performance Analyzer). Автоматически не показывается, если включён Dubs Performance Analyzer." + (FpsTpsCounter.Available ? "" : " Сейчас скрыт: Dubs Performance Analyzer активен."));

            if (cfg.FixRotStorage != sRot || cfg.FixFarMap != sMap || cfg.FixPawnEffects != sEff || cfg.FixMothballHediffs != sMoth || cfg.MarkerBadges != sBadge || cfg.FixCameraCache != sCam || cfg.ShowFpsTps != sFps)
            {
                sRot = cfg.FixRotStorage; sMap = cfg.FixFarMap; sEff = cfg.FixPawnEffects; sMoth = cfg.FixMothballHediffs; sBadge = cfg.MarkerBadges; sCam = cfg.FixCameraCache; sFps = cfg.ShowFpsTps;
                cfg.Save();
                PerfFixes.Reload(cfg);
            }

            l.Gap(14f);
            l.Label("Запись производительности: Ctrl+F9. Остальные параметры (длительность записи, цвета карты, пороги скорости) в config.txt: " + ProbeConfig.Dir);
            l.End();
            Widgets.EndScrollView();
        }

        public override void WriteSettings()
        {
            if (cfg != null) cfg.Save();
            base.WriteSettings();
        }
    }
}
