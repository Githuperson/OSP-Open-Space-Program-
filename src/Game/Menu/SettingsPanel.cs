using System;
using Godot;
using OSP.Game.App;
using OSP.Game.UI;

namespace OSP.Game.Menu
{
    /// <summary>Settings: interface scale, graphics quality, render scale, shadows, vsync, audio, controls and key bindings.</summary>
    public partial class SettingsPanel : PanelContainer
    {
        private Action _onClose;
        private string _rebinding;
        private Button _rebindButton;

        public static SettingsPanel Create(Action onClose)
        {
            var p = new SettingsPanel { _onClose = onClose, Theme = Ui.Theme, CustomMinimumSize = new Vector2(560, 0) };
            p.Build();
            return p;
        }

        private void Build()
        {
            GameSettings s = GameRoot.Instance.Settings;
            var box = Ui.VBox(6);
            box.AddChild(Ui.Label("Settings", 20, Ui.Accent));

            HBoxContainer Slider(string name, double min, double max, double step, double value, Action<double> set, Func<double, string> fmt)
            {
                HBoxContainer row = Ui.HBox(8);
                Label l = Ui.Label(name, 13, Ui.TextDim);
                l.CustomMinimumSize = new Vector2(150, 0);
                var slider = new HSlider { MinValue = min, MaxValue = max, Step = step, Value = value, CustomMinimumSize = new Vector2(220, 0) };
                Label v = Ui.Label(fmt(value), 13);
                slider.ValueChanged += x => { set(x); v.Text = fmt(x); };
                row.AddChild(l);
                row.AddChild(slider);
                row.AddChild(v);
                box.AddChild(row);
                return row;
            }

            Slider("Interface scale", 0.6, 2.0, 0.05, s.UiScale, x => { s.UiScale = x; GameRoot.Instance.ApplyDisplaySettings(); }, x => $"{x:0.00}×");
            Slider("3D render scale", 0.5, 1.0, 0.05, s.RenderScale, x => { s.RenderScale = x; GameRoot.Instance.ApplyDisplaySettings(); }, x => $"{x * 100:0}%");
            Slider("Mouse sensitivity", 0.2, 3.0, 0.1, s.MouseSensitivity, x => s.MouseSensitivity = x, x => $"{x:0.0}");
            Slider("Master volume", 0, 1, 0.05, s.MasterVolume, x => { s.MasterVolume = x; GameRoot.Instance.ApplyDisplaySettings(); }, x => $"{x * 100:0}%");

            HBoxContainer q = Ui.HBox(8);
            Label ql = Ui.Label("Graphics quality", 13, Ui.TextDim);
            ql.CustomMinimumSize = new Vector2(150, 0);
            q.AddChild(ql);
            var opt = new OptionButton();
            foreach (string n in Enum.GetNames<GraphicsQuality>()) opt.AddItem(n);
            opt.Selected = (int)s.Quality;
            opt.ItemSelected += i => { s.Quality = (GraphicsQuality)(int)i; GameRoot.Instance.ApplyDisplaySettings(); };
            q.AddChild(opt);
            box.AddChild(q);

            CheckBox Check(string name, bool value, Action<bool> set)
            {
                var c = new CheckBox { Text = name, ButtonPressed = value };
                c.Toggled += b => set(b);
                box.AddChild(c);
                return c;
            }
            Check("Shadows (applies to the next flight)", s.Shadows, b => s.Shadows = b);
            Check("Vertical sync", s.VSync, b => { s.VSync = b; GameRoot.Instance.ApplyDisplaySettings(); });
            Check("Invert pitch", s.InvertPitch, b => s.InvertPitch = b);

            box.AddChild(Ui.Label("Key bindings (click, then press a key; Esc cancels)", 14, Ui.Accent));
            var scroll = new ScrollContainer { CustomMinimumSize = new Vector2(520, 260) };
            var grid = new GridContainer { Columns = 2 };
            grid.AddThemeConstantOverride("h_separation", 12);
            foreach (InputBindings.ActionInfo a in InputBindings.Actions)
            {
                grid.AddChild(Ui.Label($"{a.Group}: {a.Description}", 13, Ui.TextDim));
                Button b = Ui.Button(InputBindings.KeyName(a.Name), null);
                string action = a.Name;
                b.CustomMinimumSize = new Vector2(150, 0);
                b.Pressed += () => { _rebinding = action; _rebindButton = b; b.Text = "press a key…"; };
                grid.AddChild(b);
            }
            scroll.AddChild(grid);
            box.AddChild(scroll);

            HBoxContainer buttons = Ui.HBox(8);
            buttons.AddChild(Ui.Button("Reset key bindings", () =>
            {
                s.KeyBindings.Clear();
                InputBindings.Apply(s);
                _onClose?.Invoke();
            }));
            buttons.AddChild(Ui.Button("Save & close", () =>
            {
                GameRoot.Instance.SaveSettings();
                _onClose?.Invoke();
            }));
            box.AddChild(buttons);
            AddChild(box);
        }

        public override void _Input(InputEvent ev)
        {
            if (_rebinding == null || ev is not InputEventKey { Pressed: true } key) return;
            GetViewport().SetInputAsHandled();
            if (key.PhysicalKeycode == Key.Escape)
            {
                _rebindButton.Text = InputBindings.KeyName(_rebinding);
                _rebinding = null;
                return;
            }
            GameSettings s = GameRoot.Instance.Settings;
            s.KeyBindings[_rebinding] = OS.GetKeycodeString(key.PhysicalKeycode);
            InputBindings.Apply(s);
            _rebindButton.Text = InputBindings.KeyName(_rebinding);
            _rebinding = null;
        }
    }
}
