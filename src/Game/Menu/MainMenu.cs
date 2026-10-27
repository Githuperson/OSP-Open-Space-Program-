using System.Linq;
using Godot;
using OSP.Core.Science;
using OSP.Game.App;
using OSP.Game.Rendering;
using OSP.Game.UI;

namespace OSP.Game.Menu
{
    /// <summary>Title screen (scenes/MainMenu.tscn): new game modes, load game, settings, controls, quit.</summary>
    public partial class MainMenu : Control
    {
        private Control _overlay;
        private ScaledSpace _backdrop;
        private double _t;

        public override void _Ready()
        {
            Theme = Ui.Theme;
            Ui.FullRect(this);
            GameRoot game = GameRoot.Instance;

            // A slowly turning view of the home world as the backdrop.
            _backdrop = ScaledSpace.Create(game.SolarSystem);
            AddChild(_backdrop);
            var bg = new TextureRect { Texture = _backdrop.Viewport.GetTexture(), ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.Scale, MouseFilter = MouseFilterEnum.Ignore };
            Ui.FullRect(bg);
            AddChild(bg);

            var col = Ui.VBox(10);
            Ui.Anchor(col, 0, 0.5f, new Vector2(70, 0), GrowDirection.End, GrowDirection.Both);
            col.AddChild(Ui.Label("OPEN SPACE PROGRAM", 40, Colors.White));
            col.AddChild(Ui.Label("An original spaceflight sandbox", 16, Ui.Accent));
            col.AddChild(new Control { CustomMinimumSize = new Vector2(0, 16) });
            void Btn(string text, System.Action a, string tip)
            {
                Button b = Ui.Button(text, a, tip);
                b.CustomMinimumSize = new Vector2(300, 38);
                b.AddThemeFontSizeOverride("font_size", 17);
                col.AddChild(b);
            }
            Btn("New Sandbox Game", () => game.NewGame(GameMode.Sandbox), "Every part unlocked, no money");
            Btn("New Science Game", () => game.NewGame(GameMode.Science), "Earn science to research new parts");
            Btn("New Career Game", () => game.NewGame(GameMode.Career), "Science, funding and contracts");
            Btn("Load Game", ShowLoad, "Continue a saved game");
            Btn("Settings", () => ShowOverlay(SettingsPanel.Create(CloseOverlay)), "Graphics, interface and key bindings");
            Btn("Controls", () => ShowOverlay(ControlsReference.Create(CloseOverlay)), "Keyboard and mouse reference");
            Btn("Quit", () => GetTree().Quit(), null);
            AddChild(col);

            if (game.DataErrors.Count > 0)
            {
                Label warn = Ui.Label("Some game data failed to load:\n" + string.Join("\n", game.DataErrors.Take(6)), 12, Ui.Warn);
                Ui.Anchor(warn, 0, 1, new Vector2(20, -20), GrowDirection.End, GrowDirection.Begin);
                AddChild(warn);
            }
            Label ver = Ui.Label($"v0.4 · {game.Catalog.All.Count} parts · {game.SolarSystem.Bodies.Count} bodies · Godot {Engine.GetVersionInfo()["string"]}", 12, Ui.TextDim);
            Ui.Anchor(ver, 1, 1, new Vector2(-16, -12), GrowDirection.Begin, GrowDirection.Begin);
            AddChild(ver);
        }

        public override void _Process(double delta)
        {
            _t += delta;
            var game = GameRoot.Instance;
            var home = game.SolarSystem.HomeBody;
            double time = 4000 + _t * 120;
            // Camera hanging off the home world's day side.
            Core.Math.Vector3d sun = (game.SolarSystem.Root.AbsolutePosition(time) - home.AbsolutePosition(time)).Normalized();
            Core.Math.Vector3d side = Core.Math.Vector3d.Cross(Core.Math.Vector3d.UnitY, sun).Normalized();
            Core.Math.Vector3d camDir = (sun * 0.55 + side * 0.8 + Core.Math.Vector3d.UnitY * 0.25).Normalized();
            Core.Math.Vector3d origin = home.AbsolutePosition(time) + camDir * home.Radius * 2.6;
            Basis basis = Basis.LookingAt((-camDir + side * 0.35).Normalized().ToGodot(), Vector3.Up);
            _backdrop.Sync(time, origin, Core.Math.Vector3d.Zero, basis, 50);
            Vector2I px = DisplayServer.WindowGetSize();
            _backdrop.Resize(new Vector2I(System.Math.Max(64, px.X), System.Math.Max(64, px.Y)));
        }

        private void ShowOverlay(Control c)
        {
            CloseOverlay();
            var dim = new ColorRect { Color = new Color(0, 0, 0, 0.5f), MouseFilter = MouseFilterEnum.Stop };
            Ui.FullRect(dim);
            var center = new CenterContainer();
            Ui.FullRect(center);
            center.AddChild(c);
            dim.AddChild(center);
            _overlay = dim;
            AddChild(dim);
        }

        private void CloseOverlay()
        {
            _overlay?.QueueFree();
            _overlay = null;
        }

        private void ShowLoad()
        {
            GameRoot game = GameRoot.Instance;
            var box = Ui.VBox(6);
            box.AddChild(Ui.Label("Load game", 20, Ui.Accent));
            var saves = game.ListSaves().ToList();
            if (saves.Count == 0) box.AddChild(Ui.Label("No saved games yet.", 13, Ui.TextDim));
            var status = Ui.Label("", 12, Ui.Bad);
            foreach (string s in saves)
            {
                string name = s;
                box.AddChild(Ui.Button(name, () =>
                {
                    try
                    {
                        foreach (string w in game.LoadGame(name)) GD.PushWarning(w);
                        if (game.World.ActiveVessel != null) game.ResumeFlight();
                        else game.GoToEditor();
                    }
                    catch (System.Exception ex) when (ex is System.FormatException or System.InvalidOperationException)
                    {
                        status.Text = ex.Message;
                    }
                }));
            }
            box.AddChild(status);
            box.AddChild(Ui.Button("Back", CloseOverlay));
            var p = Ui.Panel(box);
            p.CustomMinimumSize = new Vector2(380, 0);
            ShowOverlay(p);
        }
    }
}
