using System;
using Godot;

namespace OSP.Game.UI;

/// <summary>Small immediate-style UI toolkit: a dark aerospace theme and helpers that build Control trees in code.</summary>
public static class Ui
{
    public static readonly Color Accent = new(0.30f, 0.82f, 0.95f);
    public static readonly Color Good = new(0.45f, 0.9f, 0.5f);
    public static readonly Color Warn = new(1.0f, 0.65f, 0.2f);
    public static readonly Color Bad = new(1.0f, 0.35f, 0.3f);
    public static readonly Color Dim = new(0.62f, 0.68f, 0.76f);
    public static readonly Color Text = new(0.92f, 0.95f, 0.98f);
    public static readonly Color PanelBg = new(0.04f, 0.06f, 0.10f, 0.86f);

    private static Theme? _theme;
    public static Theme Theme => _theme ??= CreateTheme();

    private static StyleBoxFlat Box(Color bg, Color border, int radius = 5, int borderW = 1, int pad = 8)
    {
        var s = new StyleBoxFlat { BgColor = bg, BorderColor = border };
        s.SetBorderWidthAll(borderW); s.SetCornerRadiusAll(radius);
        s.ContentMarginLeft = pad + 2; s.ContentMarginRight = pad + 2; s.ContentMarginTop = pad - 2; s.ContentMarginBottom = pad - 2;
        return s;
    }

    private static Theme CreateTheme()
    {
        var t = new Theme { DefaultFontSize = 16 };
        t.SetColor("font_color", "Label", Text);
        t.SetStylebox("panel", "PanelContainer", Box(PanelBg, new Color(0.2f, 0.35f, 0.45f, 0.9f), 6, 1, 8));
        t.SetStylebox("panel", "Panel", Box(PanelBg, new Color(0.2f, 0.35f, 0.45f, 0.9f), 6, 1, 8));
        foreach (var c in new[] { "Button", "OptionButton", "CheckButton", "MenuButton" })
        {
            t.SetStylebox("normal", c, Box(new Color(0.10f, 0.15f, 0.23f, 0.95f), new Color(0.25f, 0.4f, 0.55f), 5, 1, 7));
            t.SetStylebox("hover", c, Box(new Color(0.16f, 0.25f, 0.37f, 1f), Accent, 5, 1, 7));
            t.SetStylebox("pressed", c, Box(new Color(0.08f, 0.35f, 0.45f, 1f), Accent, 5, 2, 7));
            t.SetStylebox("disabled", c, Box(new Color(0.07f, 0.09f, 0.12f, 0.8f), new Color(0.2f, 0.25f, 0.3f), 5, 1, 7));
            t.SetStylebox("focus", c, new StyleBoxEmpty());
            t.SetColor("font_color", c, Text); t.SetColor("font_hover_color", c, Colors.White);
            t.SetColor("font_disabled_color", c, new Color(0.45f, 0.5f, 0.55f));
        }
        t.SetStylebox("normal", "LineEdit", Box(new Color(0.03f, 0.05f, 0.08f, 0.95f), new Color(0.25f, 0.4f, 0.55f), 4, 1, 6));
        t.SetStylebox("focus", "LineEdit", Box(new Color(0.03f, 0.05f, 0.08f, 0.95f), Accent, 4, 1, 6));
        t.SetColor("font_color", "LineEdit", Text);
        t.SetStylebox("panel", "ItemList", Box(new Color(0.03f, 0.05f, 0.08f, 0.9f), new Color(0.2f, 0.3f, 0.4f), 4, 1, 4));
        t.SetStylebox("selected", "ItemList", Box(new Color(0.1f, 0.4f, 0.5f, 0.9f), Accent, 3, 1, 3));
        t.SetStylebox("selected_focus", "ItemList", Box(new Color(0.1f, 0.4f, 0.5f, 0.9f), Accent, 3, 1, 3));
        t.SetStylebox("panel", "Tree", Box(new Color(0.03f, 0.05f, 0.08f, 0.9f), new Color(0.2f, 0.3f, 0.4f), 4, 1, 4));
        t.SetStylebox("selected", "Tree", Box(new Color(0.1f, 0.4f, 0.5f, 0.9f), Accent, 3, 1, 3));
        t.SetStylebox("selected_focus", "Tree", Box(new Color(0.1f, 0.4f, 0.5f, 0.9f), Accent, 3, 1, 3));
        t.SetColor("font_color", "Tree", Text); t.SetColor("font_color", "ItemList", Text);
        t.SetStylebox("panel", "TabContainer", Box(new Color(0.03f, 0.05f, 0.08f, 0.5f), new Color(0.2f, 0.3f, 0.4f), 4, 1, 4));
        t.SetStylebox("tab_selected", "TabContainer", Box(new Color(0.1f, 0.3f, 0.4f, 1f), Accent, 4, 1, 5));
        t.SetStylebox("tab_unselected", "TabContainer", Box(new Color(0.07f, 0.1f, 0.15f, 1f), new Color(0.2f, 0.3f, 0.4f), 4, 1, 5));
        t.SetStylebox("tab_hovered", "TabContainer", Box(new Color(0.12f, 0.2f, 0.3f, 1f), Accent, 4, 1, 5));
        t.SetColor("font_selected_color", "TabContainer", Colors.White);
        t.SetColor("font_unselected_color", "TabContainer", Dim);
        t.SetFontSize("font_size", "TabContainer", 14);
        t.SetStylebox("background", "ProgressBar", Box(new Color(0.02f, 0.03f, 0.05f, 0.9f), new Color(0.2f, 0.3f, 0.4f), 3, 1, 0));
        t.SetStylebox("fill", "ProgressBar", Box(new Color(0.3f, 0.78f, 0.95f, 0.95f), new Color(0.3f, 0.78f, 0.95f), 3, 0, 0));
        return t;
    }

    public static Label Label(string text, int size = 16, Color? color = null, bool wrap = false)
    {
        var l = new Label { Text = text, AutowrapMode = wrap ? TextServer.AutowrapMode.WordSmart : TextServer.AutowrapMode.Off };
        l.AddThemeFontSizeOverride("font_size", size);
        if (color.HasValue) l.AddThemeColorOverride("font_color", color.Value);
        return l;
    }

    public static Button Button(string text, Action? onPress = null, string? tooltip = null, int minWidth = 0)
    {
        var b = new Button { Text = text, TooltipText = tooltip ?? "", CustomMinimumSize = new Vector2(minWidth, 0), FocusMode = Control.FocusModeEnum.None };
        if (onPress != null) b.Pressed += onPress;
        return b;
    }

    public static VBoxContainer VBox(int sep = 6) { var v = new VBoxContainer(); v.AddThemeConstantOverride("separation", sep); return v; }
    public static HBoxContainer HBox(int sep = 6) { var h = new HBoxContainer(); h.AddThemeConstantOverride("separation", sep); return h; }

    public static PanelContainer Panel(Control content, int margin = 4)
    {
        var p = new PanelContainer();
        var m = new MarginContainer();
        m.AddThemeConstantOverride("margin_left", margin); m.AddThemeConstantOverride("margin_right", margin);
        m.AddThemeConstantOverride("margin_top", margin); m.AddThemeConstantOverride("margin_bottom", margin);
        m.AddChild(content); p.AddChild(m);
        return p;
    }

    public static Control Spacer(float h = 8) => new Control { CustomMinimumSize = new Vector2(0, h) };
    public static Control Filler() => new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ExpandFill };

    public static void Anchor(Control c, Control.LayoutPreset preset, int margin = 12)
    {
        c.SetAnchorsAndOffsetsPreset(preset, Control.LayoutPresetMode.Minsize, margin);
    }

    public static HSeparator Sep() => new HSeparator();

    public static string FormatDistance(double m)
    {
        double a = Math.Abs(m);
        if (a >= 1e9) return $"{m / 1e9:F2} Gm";
        if (a >= 1e6) return $"{m / 1e6:F2} Mm";
        if (a >= 1e4) return $"{m / 1e3:F1} km";
        if (a >= 1e3) return $"{m / 1e3:F2} km";
        return $"{m:F1} m";
    }

    public static string FormatSpeed(double v)
    {
        double a = Math.Abs(v);
        if (a >= 1e4) return $"{v / 1e3:F2} km/s";
        return $"{v:F1} m/s";
    }

    public static string FormatMass(double kg) => Math.Abs(kg) >= 10000 ? $"{kg / 1000:F2} t" : (Math.Abs(kg) >= 1000 ? $"{kg / 1000:F3} t" : $"{kg:F0} kg");

    public static string FormatTime(double seconds)
    {
        if (double.IsNaN(seconds) || double.IsInfinity(seconds)) return "--";
        bool neg = seconds < 0; seconds = Math.Abs(seconds);
        // universal calendar: 6-hour days, 426-day years would need more config; show d/h/m/s with 6 h days (Aldara rotation).
        long s = (long)seconds;
        long y = s / (426 * 21600); s %= 426 * 21600;
        long d = s / 21600; s %= 21600;
        long h = s / 3600; s %= 3600; long m = s / 60; s %= 60;
        string r = y > 0 ? $"{y}y {d}d {h:D2}:{m:D2}:{s:D2}" : d > 0 ? $"{d}d {h:D2}:{m:D2}:{s:D2}" : h > 0 ? $"{h}:{m:D2}:{s:D2}" : m > 0 ? $"{m}:{s:D2}" : $"{s}s";
        return neg ? "-" + r : r;
    }
}
