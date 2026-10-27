using System;
using Godot;

namespace OSP.Game.UI
{
    /// <summary>Shared look-and-feel and widget helpers. All UI is built in code from these pieces.</summary>
    public static class Ui
    {
        public static readonly Color Accent = new(0.32f, 0.78f, 0.98f);
        public static readonly Color AccentDim = new(0.2f, 0.45f, 0.6f);
        public static readonly Color Text = new(0.9f, 0.93f, 0.96f);
        public static readonly Color TextDim = new(0.6f, 0.66f, 0.72f);
        public static readonly Color Good = new(0.4f, 0.9f, 0.5f);
        public static readonly Color Warn = new(1.0f, 0.75f, 0.25f);
        public static readonly Color Bad = new(1.0f, 0.35f, 0.3f);
        public static readonly Color PanelBg = new(0.055f, 0.07f, 0.095f, 0.88f);

        private static Theme _theme;

        public static Theme Theme => _theme ??= BuildTheme();

        private static StyleBoxFlat Box(Color bg, Color border, int borderWidth = 1, int radius = 6, int margin = 8)
        {
            var sb = new StyleBoxFlat
            {
                BgColor = bg,
                BorderColor = border,
                CornerRadiusBottomLeft = radius,
                CornerRadiusBottomRight = radius,
                CornerRadiusTopLeft = radius,
                CornerRadiusTopRight = radius,
                ContentMarginLeft = margin,
                ContentMarginRight = margin,
                ContentMarginTop = margin * 0.7f,
                ContentMarginBottom = margin * 0.7f,
            };
            sb.SetBorderWidthAll(borderWidth);
            return sb;
        }

        private static Theme BuildTheme()
        {
            var t = new Theme { DefaultFontSize = 14 };
            t.SetStylebox("panel", "PanelContainer", Box(PanelBg, new Color(0.25f, 0.35f, 0.45f, 0.6f)));
            t.SetStylebox("panel", "Panel", Box(PanelBg, new Color(0.25f, 0.35f, 0.45f, 0.6f)));
            t.SetStylebox("normal", "Button", Box(new Color(0.12f, 0.16f, 0.21f, 0.95f), new Color(0.3f, 0.42f, 0.55f), 1, 4, 8));
            t.SetStylebox("hover", "Button", Box(new Color(0.17f, 0.24f, 0.32f, 0.95f), Accent, 1, 4, 8));
            t.SetStylebox("pressed", "Button", Box(new Color(0.1f, 0.35f, 0.5f, 0.95f), Accent, 1, 4, 8));
            t.SetStylebox("disabled", "Button", Box(new Color(0.1f, 0.11f, 0.13f, 0.8f), new Color(0.2f, 0.22f, 0.25f), 1, 4, 8));
            t.SetStylebox("focus", "Button", Box(new Color(0, 0, 0, 0), Accent, 1, 4, 8));
            t.SetColor("font_color", "Button", Text);
            t.SetColor("font_hover_color", "Button", Colors.White);
            t.SetColor("font_pressed_color", "Button", Colors.White);
            t.SetColor("font_disabled_color", "Button", TextDim * 0.8f);
            t.SetColor("font_color", "Label", Text);
            t.SetStylebox("normal", "LineEdit", Box(new Color(0.04f, 0.05f, 0.07f, 0.95f), new Color(0.3f, 0.42f, 0.55f), 1, 4, 6));
            t.SetStylebox("focus", "LineEdit", Box(new Color(0.04f, 0.05f, 0.07f, 0.95f), Accent, 1, 4, 6));
            t.SetStylebox("panel", "Tree", Box(new Color(0.04f, 0.05f, 0.07f, 0.9f), new Color(0.2f, 0.3f, 0.4f), 1, 4, 4));
            t.SetStylebox("panel", "ItemList", Box(new Color(0.04f, 0.05f, 0.07f, 0.9f), new Color(0.2f, 0.3f, 0.4f), 1, 4, 4));
            t.SetStylebox("panel", "TabContainer", Box(PanelBg, new Color(0.25f, 0.35f, 0.45f, 0.6f)));
            t.SetStylebox("background", "ProgressBar", Box(new Color(0.05f, 0.06f, 0.08f, 1), new Color(0.2f, 0.25f, 0.3f), 1, 3, 0));
            t.SetStylebox("fill", "ProgressBar", Box(Accent * 0.9f, Accent, 0, 3, 0));
            t.SetColor("font_color", "TooltipLabel", Text);
            t.SetStylebox("panel", "TooltipPanel", Box(new Color(0.03f, 0.04f, 0.06f, 0.97f), Accent, 1, 4, 8));
            return t;
        }

        public static PanelContainer Panel(Control content = null)
        {
            var p = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Stop };
            if (content != null) p.AddChild(content);
            return p;
        }

        public static Label Label(string text, int size = 14, Color? color = null, HorizontalAlignment align = HorizontalAlignment.Left)
        {
            var l = new Label { Text = text, HorizontalAlignment = align, MouseFilter = Control.MouseFilterEnum.Ignore };
            if (size != 14) l.AddThemeFontSizeOverride("font_size", size);
            if (color.HasValue) l.AddThemeColorOverride("font_color", color.Value);
            return l;
        }

        public static Label Heading(string text) => Label(text, 16, Accent);

        public static Button Button(string text, Action onPressed, string tooltip = null, bool toggle = false)
        {
            var b = new Button { Text = text, TooltipText = tooltip ?? "", ToggleMode = toggle, FocusMode = Control.FocusModeEnum.None };
            if (onPressed != null) b.Pressed += onPressed;
            return b;
        }

        public static HBoxContainer HBox(int separation = 6)
        {
            var h = new HBoxContainer();
            h.AddThemeConstantOverride("separation", separation);
            return h;
        }

        public static VBoxContainer VBox(int separation = 4)
        {
            var v = new VBoxContainer();
            v.AddThemeConstantOverride("separation", separation);
            return v;
        }

        public static ProgressBar Bar(Color fill)
        {
            var b = new ProgressBar { MinValue = 0, MaxValue = 1, ShowPercentage = false, CustomMinimumSize = new Vector2(90, 10) };
            var sb = new StyleBoxFlat { BgColor = fill };
            sb.SetCornerRadiusAll(3);
            b.AddThemeStyleboxOverride("fill", sb);
            return b;
        }

        /// <summary>A two-column "name: value" row; returns the value label.</summary>
        public static Label Row(Container parent, string name, int nameWidth = 110)
        {
            HBoxContainer h = HBox(4);
            Label n = Label(name, 13, TextDim);
            n.CustomMinimumSize = new Vector2(nameWidth, 0);
            Label v = Label("—", 14);
            v.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            h.AddChild(n);
            h.AddChild(v);
            parent.AddChild(h);
            return v;
        }

        // ----- Formatting (SI units) -----------------------------------------------------------------------

        public static string Distance(double m)
        {
            if (double.IsNaN(m)) return "—";
            if (double.IsInfinity(m)) return "∞";
            double a = Math.Abs(m);
            if (a < 10000) return $"{m:N0} m";
            if (a < 1e7) return $"{m / 1000:N1} km";
            if (a < 1e10) return $"{m / 1e6:N2} Mm";
            return $"{m / 1e9:N2} Gm";
        }

        public static string Speed(double ms) => double.IsNaN(ms) ? "—" : (Math.Abs(ms) < 10000 ? $"{ms:N1} m/s" : $"{ms / 1000:N2} km/s");
        public static string Mass(double kg) => kg < 1000 ? $"{kg:N0} kg" : $"{kg / 1000:N2} t";
        public static string Force(double n) => n < 10000 ? $"{n:N0} N" : (n < 1e7 ? $"{n / 1000:N1} kN" : $"{n / 1e6:N2} MN");
        public static string Pressure(double pa) => pa < 1000 ? $"{pa:N1} Pa" : $"{pa / 1000:N2} kPa";

        public const double DaySeconds = 21600; // one Aster day (6 h)
        public const double YearDays = 426;

        /// <summary>Duration such as "2d 03h 04m 05s".</summary>
        public static string Duration(double s)
        {
            if (double.IsNaN(s) || double.IsInfinity(s)) return "—";
            bool neg = s < 0;
            s = Math.Abs(s);
            long total = (long)Math.Floor(s);
            long days = total / (long)DaySeconds;
            long rem = total % (long)DaySeconds;
            long h = rem / 3600, m = rem % 3600 / 60, sec = rem % 60;
            string r;
            if (days >= 1000) r = $"{days / YearDays:N1}y";
            else if (days > 0) r = $"{days}d {h:00}h {m:00}m";
            else if (h > 0) r = $"{h}h {m:00}m {sec:00}s";
            else if (m > 0) r = $"{m}m {sec:00}s";
            else r = $"{s:0.0}s";
            return (neg ? "-" : "") + r;
        }

        /// <summary>Calendar date for a universal time: "Y1 D001 00:00:00" (6-hour days, 426-day years).</summary>
        public static string Date(double ut)
        {
            long total = (long)Math.Floor(Math.Max(0, ut));
            long days = total / (long)DaySeconds;
            long year = days / (long)YearDays + 1;
            long day = days % (long)YearDays + 1;
            long rem = total % (long)DaySeconds;
            return $"Y{year} D{day:000} {rem / 3600:00}:{rem % 3600 / 60:00}:{rem % 60:00}";
        }

        /// <summary>
        /// Pins a control to an anchor point of its parent (0..1 in each axis) with a pixel offset, growing in the given
        /// directions as its content requires. Robust to content that only gets its size after entering the tree.
        /// </summary>
        public static void Anchor(Control c, float ax, float ay, Vector2 offset, Control.GrowDirection growH, Control.GrowDirection growV)
        {
            c.AnchorLeft = c.AnchorRight = ax;
            c.AnchorTop = c.AnchorBottom = ay;
            c.OffsetLeft = c.OffsetRight = offset.X;
            c.OffsetTop = c.OffsetBottom = offset.Y;
            c.GrowHorizontal = growH;
            c.GrowVertical = growV;
        }

        public static void FullRect(Control c)
        {
            c.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            c.OffsetLeft = c.OffsetTop = c.OffsetRight = c.OffsetBottom = 0;
        }
    }
}
