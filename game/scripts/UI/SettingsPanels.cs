using System;
using System.Linq;
using Godot;

namespace OSP.Game.UI;

/// <summary>Settings, key-binding and controls-reference panels (shared by the main menu and the pause menu).</summary>
public static class SettingsPanels
{
    public static Control BuildSettings(Action onApplied)
    {
        var gs = GameState.Instance; var s = gs.Settings;
        var v = Ui.VBox(8);
        var grid = new GridContainer { Columns = 2 };
        grid.AddThemeConstantOverride("h_separation", 16); grid.AddThemeConstantOverride("v_separation", 8);

        var quality = new OptionButton();
        quality.AddItem("Low (fast)"); quality.AddItem("Medium"); quality.AddItem("High");
        quality.Selected = Math.Clamp(s.GraphicsQuality, 0, 2);
        grid.AddChild(Ui.Label("Graphics quality")); grid.AddChild(quality);

        var scale = new HSlider { MinValue = 0.5, MaxValue = 1.0, Step = 0.05, Value = s.RenderScale, CustomMinimumSize = new Vector2(200, 0) };
        var scaleLbl = Ui.Label($"{s.RenderScale:P0}");
        scale.ValueChanged += val => scaleLbl.Text = $"{val:P0}";
        var scaleRow = Ui.HBox(); scaleRow.AddChild(scale); scaleRow.AddChild(scaleLbl);
        grid.AddChild(Ui.Label("3D render resolution")); grid.AddChild(scaleRow);

        var fov = new HSlider { MinValue = 50, MaxValue = 100, Step = 1, Value = s.FieldOfView, CustomMinimumSize = new Vector2(200, 0) };
        var fovLbl = Ui.Label($"{s.FieldOfView:F0}°");
        fov.ValueChanged += val => fovLbl.Text = $"{val:F0}°";
        var fovRow = Ui.HBox(); fovRow.AddChild(fov); fovRow.AddChild(fovLbl);
        grid.AddChild(Ui.Label("Field of view")); grid.AddChild(fovRow);

        var sens = new HSlider { MinValue = 0.2, MaxValue = 3.0, Step = 0.1, Value = s.MouseSensitivity, CustomMinimumSize = new Vector2(200, 0) };
        var sensLbl = Ui.Label($"{s.MouseSensitivity:F1}");
        sens.ValueChanged += val => sensLbl.Text = $"{val:F1}";
        var sensRow = Ui.HBox(); sensRow.AddChild(sens); sensRow.AddChild(sensLbl);
        grid.AddChild(Ui.Label("Mouse sensitivity")); grid.AddChild(sensRow);

        var vsync = new CheckButton { ButtonPressed = s.Vsync, Text = "On" };
        grid.AddChild(Ui.Label("Vertical sync")); grid.AddChild(vsync);
        var full = new CheckButton { ButtonPressed = s.Fullscreen, Text = "On" };
        grid.AddChild(Ui.Label("Fullscreen")); grid.AddChild(full);
        v.AddChild(grid);
        v.AddChild(Ui.Label("Quality affects terrain detail, shadows, atmosphere sampling and anti-aliasing. Lower the render resolution on 8 GB Macs if the frame rate drops.", 13, Ui.Dim, true));

        var row = Ui.HBox();
        row.AddChild(Ui.Button("Apply & save", () =>
        {
            s.GraphicsQuality = quality.Selected; s.RenderScale = scale.Value; s.FieldOfView = fov.Value;
            s.MouseSensitivity = sens.Value; s.Vsync = vsync.ButtonPressed; s.Fullscreen = full.ButtonPressed;
            gs.SaveSettings(); gs.ApplyGraphicsSettings(); onApplied();
        }, null, 140));
        v.AddChild(row);
        return v;
    }

    public static Control BuildKeyBindings()
    {
        var gs = GameState.Instance;
        var scroll = new ScrollContainer { CustomMinimumSize = new Vector2(620, 420) };
        var list = Ui.VBox(4);
        scroll.AddChild(list);
        Button? waiting = null; string? waitingId = null;
        var holder = new KeyCapture();
        scroll.AddChild(holder);
        string lastGroup = "";
        foreach (var d in Bindings.Defs)
        {
            if (d.Group != lastGroup) { list.AddChild(Ui.Label(d.Group, 17, Ui.Accent)); lastGroup = d.Group; }
            var row = Ui.HBox();
            var name = Ui.Label(d.Label); name.CustomMinimumSize = new Vector2(300, 0);
            row.AddChild(name);
            var btn = Ui.Button(Bindings.KeyName(gs.Keys.Get(d.Id)), null, "Click, then press the new key", 140);
            var id = d.Id;
            btn.Pressed += () =>
            {
                if (waiting != null) waiting.Text = Bindings.KeyName(gs.Keys.Get(waitingId!));
                waiting = btn; waitingId = id; btn.Text = "press a key…";
                holder.Begin(k =>
                {
                    if (k != Key.Escape) { gs.Keys.Set(id, k); }
                    btn.Text = Bindings.KeyName(gs.Keys.Get(id)); waiting = null; gs.SaveSettings();
                });
            };
            row.AddChild(btn);
            list.AddChild(row);
        }
        var reset = Ui.Button("Reset all to defaults", () => { gs.Keys.ResetAll(); gs.SaveSettings(); });
        list.AddChild(Ui.Spacer()); list.AddChild(reset);
        return scroll;
    }

    public static Control BuildControlsReference()
    {
        var gs = GameState.Instance;
        var scroll = new ScrollContainer { CustomMinimumSize = new Vector2(780, 460) };
        var cols = new HBoxContainer(); cols.AddThemeConstantOverride("separation", 28);
        scroll.AddChild(cols);

        var left = Ui.VBox(3); var right = Ui.VBox(3);
        cols.AddChild(left); cols.AddChild(right);
        string lastGroup = "";
        int n = 0, half = Bindings.Defs.Length / 2;
        foreach (var d in Bindings.Defs)
        {
            var target = n++ < half ? left : right;
            if (d.Group != lastGroup) { target.AddChild(Ui.Label(d.Group, 16, Ui.Accent)); lastGroup = d.Group; }
            var row = Ui.HBox();
            var k = Ui.Label(Bindings.KeyName(gs.Keys.Get(d.Id)), 14, Ui.Warn); k.CustomMinimumSize = new Vector2(80, 0);
            row.AddChild(k); row.AddChild(Ui.Label(d.Label, 14));
            target.AddChild(row);
        }
        right.AddChild(Ui.Spacer(6));
        right.AddChild(Ui.Label("Mouse (flight & map)", 16, Ui.Accent));
        foreach (var t in new[] { "Right-drag: orbit camera", "Wheel: zoom", "Middle-drag (map): pan", "Left-click (map): select / place node", "Esc: pause menu" })
            right.AddChild(Ui.Label(t, 14));
        right.AddChild(Ui.Spacer(6));
        right.AddChild(Ui.Label("Vehicle Assembly", 16, Ui.Accent));
        foreach (var t in new[] { "Click part in list, click to place (snaps to nodes)", "Right-drag: orbit   Middle-drag: pan   Wheel: zoom", "Q / E: rotate held part   F: flip   X: symmetry", "Delete: remove selected   Ctrl+Z / Ctrl+Y: undo / redo", "Ctrl+D: duplicate selected   M: pick up & move selected", "Esc: drop held part" })
            right.AddChild(Ui.Label(t, 14));
        return scroll;
    }
}

/// <summary>Captures the next key press for rebinding.</summary>
public sealed partial class KeyCapture : Control
{
    private Action<Key>? _cb;
    public void Begin(Action<Key> cb) { _cb = cb; SetProcessUnhandledKeyInput(true); }
    public override void _UnhandledKeyInput(InputEvent e)
    {
        if (_cb != null && e is InputEventKey k && k.Pressed && !k.Echo)
        {
            var cb = _cb; _cb = null; SetProcessUnhandledKeyInput(false);
            GetViewport().SetInputAsHandled();
            cb(k.Keycode);
        }
    }
}
