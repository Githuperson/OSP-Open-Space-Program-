using System.Collections.Generic;
using System.Linq;
using System.Text;
using Godot;
using OSP.Core;
using OSP.Core.Flight;
using OSP.Core.Parts;
using OSP.Core.Persistence;
using OSP.Core.Science;
using OSP.Core.Vehicles;
using OSP.Game.App;
using OSP.Game.UI;

namespace OSP.Game.Editor
{
    /// <summary>Editor interface: catalogue, vessel statistics, staging, structure tree and file operations.</summary>
    public partial class EditorUi : Control
    {
        private EditorScene _editor;
        private GameRoot Game => GameRoot.Instance;

        private LineEdit _name, _search;
        private PartCategory? _category;
        private VBoxContainer _partList;
        private RichTextLabel _partInfo;
        private VBoxContainer _stats, _staging;
        private Tree _tree;
        private Label _hint, _symLabel, _funds;
        private Button _undo, _redo, _snap, _centers;
        private readonly List<Control> _panels = new();
        private Control _dialog, _help;
        private double _hintTimer;
        private string _transientHint;

        public static EditorUi Create(EditorScene editor)
        {
            var ui = new EditorUi { _editor = editor, Theme = Ui.Theme, Name = "EditorUi" };
            Ui.FullRect(ui);
            ui.MouseFilter = MouseFilterEnum.Ignore;
            ui.Build();
            editor.DesignChanged += ui.Refresh;
            ui.Refresh();
            return ui;
        }

        public bool IsMouseOverUi(Vector2 pos)
        {
            if (_dialog != null && IsInstanceValid(_dialog)) return true;
            if (_help != null && IsInstanceValid(_help)) return true;
            return _panels.Any(p => p.Visible && p.GetGlobalRect().HasPoint(pos));
        }

        private T Track<T>(T c) where T : Control
        {
            _panels.Add(c);
            return c;
        }

        private void Build()
        {
            // ---- Top bar ------------------------------------------------------------------------------------
            var bar = Ui.HBox(6);
            _name = new LineEdit { CustomMinimumSize = new Vector2(220, 0), PlaceholderText = "Vessel name" };
            _name.TextSubmitted += t => { _editor.Rename(t); _name.ReleaseFocus(); };
            _name.FocusExited += () => _editor.Rename(_name.Text);
            bar.AddChild(_name);
            bar.AddChild(Ui.Button("New", () => _editor.NewDesign(), "Start a new vessel (undoable)"));
            bar.AddChild(Ui.Button("Load…", ShowLoad, "Open a saved vessel"));
            bar.AddChild(Ui.Button("Save", SaveDesign, "Save this vessel (Ctrl+S)"));
            _undo = Ui.Button("↶ Undo", () => _editor.Undo(), "Undo (Ctrl+Z)");
            _redo = Ui.Button("↷ Redo", () => _editor.Redo(), "Redo (Ctrl+Y)");
            bar.AddChild(_undo);
            bar.AddChild(_redo);
            Button sym = Ui.Button("Symmetry", () => { _editor.CycleSymmetry(); }, "Radial symmetry for surface-attached parts (X)");
            bar.AddChild(sym);
            _symLabel = Ui.Label("×1", 14, Ui.Accent);
            bar.AddChild(_symLabel);
            _snap = Ui.Button("Angle snap", () => { _editor.AngleSnap = !_editor.AngleSnap; Refresh(); }, "Snap radial placement to 15° steps (C)", true);
            bar.AddChild(_snap);
            _centers = Ui.Button("CoM/CoT", () => { _editor.ShowCenters = !_editor.ShowCenters; Refresh(); }, "Show centre of mass (yellow) and thrust (purple)", true);
            bar.AddChild(_centers);
            if (Game.Progress.Mode != GameMode.Sandbox) bar.AddChild(Ui.Button("Research", ShowResearch, "Spend science on new technology"));
            if (Game.Progress.Mode == GameMode.Career) bar.AddChild(Ui.Button("Contracts", ShowContracts, "Accept and track contracts"));
            bar.AddChild(Ui.Button("Tracking", ShowTracking, "Vessels in flight in this game"));
            bar.AddChild(Ui.Button("Help", ToggleHelp, "Controls (F1)"));
            bar.AddChild(Ui.Button("Menu", ShowMenu, "Save game / main menu"));
            Button launch = Ui.Button("  LAUNCH  ", () => _editor.Launch(), "Roll out to the launch pad");
            launch.AddThemeColorOverride("font_color", Ui.Good);
            launch.AddThemeFontSizeOverride("font_size", 16);
            bar.AddChild(launch);
            _funds = Ui.Label("", 13, Ui.Warn);
            bar.AddChild(_funds);
            PanelContainer top = Track(Ui.Panel(bar));
            top.Position = new Vector2(8, 6);
            AddChild(top);

            // ---- Left: catalogue ---------------------------------------------------------------------------
            var cat = Ui.VBox(4);
            cat.AddChild(Ui.Heading("PARTS"));
            _search = new LineEdit { PlaceholderText = "Search parts…", ClearButtonEnabled = true };
            _search.TextChanged += _ => RefreshCatalog();
            cat.AddChild(_search);
            var cats = new GridContainer { Columns = 3 };
            cats.AddThemeConstantOverride("h_separation", 3);
            cats.AddThemeConstantOverride("v_separation", 3);
            void CatBtn(string label, PartCategory? c)
            {
                Button b = Ui.Button(label, () => { _category = c; RefreshCatalog(); }, null, true);
                b.CustomMinimumSize = new Vector2(96, 0);
                b.AddThemeFontSizeOverride("font_size", 12);
                cats.AddChild(b);
            }
            CatBtn("All", null);
            foreach (PartCategory c in System.Enum.GetValues<PartCategory>()) CatBtn(CategoryName(c), c);
            cat.AddChild(cats);
            var scroll = new ScrollContainer { CustomMinimumSize = new Vector2(300, 330), HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, SizeFlagsVertical = SizeFlags.ExpandFill };
            _partList = Ui.VBox(2);
            _partList.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            scroll.AddChild(_partList);
            cat.AddChild(scroll);
            _partInfo = new RichTextLabel { BbcodeEnabled = true, FitContent = false, CustomMinimumSize = new Vector2(300, 190), ScrollActive = true };
            cat.AddChild(_partInfo);
            PanelContainer left = Track(Ui.Panel(cat));
            left.Position = new Vector2(8, 58);
            AddChild(left);

            // ---- Right: tabs ---------------------------------------------------------------------------------
            var tabs = new TabContainer { CustomMinimumSize = new Vector2(330, 560) };
            var statsScroll = new ScrollContainer { Name = "Statistics", HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
            _stats = Ui.VBox(3);
            _stats.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            statsScroll.AddChild(_stats);
            tabs.AddChild(statsScroll);
            var stagingBox = Ui.VBox(4);
            stagingBox.Name = "Staging";
            var stagingTools = Ui.HBox(4);
            stagingTools.AddChild(Ui.Button("Auto-stage", () => _editor.RunAutoStage(), "Recompute a default staging order"));
            stagingBox.AddChild(stagingTools);
            var stagingScroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
            _staging = Ui.VBox(4);
            _staging.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            stagingScroll.AddChild(_staging);
            stagingBox.AddChild(stagingScroll);
            tabs.AddChild(stagingBox);
            _tree = new Tree { Name = "Structure", HideRoot = false, SelectMode = Tree.SelectModeEnum.Single };
            _tree.ItemSelected += () =>
            {
                TreeItem it = _tree.GetSelected();
                _editor.SelectedPartId = it == null ? -1 : (int)it.GetMetadata(0);
            };
            tabs.AddChild(_tree);
            PanelContainer right = Track(Ui.Panel(tabs));
            Ui.Anchor(right, 1, 0, new Vector2(-8, 58), GrowDirection.Begin, GrowDirection.End);
            AddChild(right);

            // ---- Bottom hint --------------------------------------------------------------------------------
            _hint = Ui.Label("", 14, Ui.Text, HorizontalAlignment.Center);
            _hint.AddThemeColorOverride("font_outline_color", Colors.Black);
            _hint.AddThemeConstantOverride("outline_size", 4);
            _hint.CustomMinimumSize = new Vector2(800, 0);
            Ui.Anchor(_hint, 0.5f, 1, new Vector2(0, -12), GrowDirection.Both, GrowDirection.Begin);
            AddChild(_hint);

            RefreshCatalog();
        }

        private static string CategoryName(PartCategory c) => c switch
        {
            PartCategory.FuelTank => "Fuel tanks",
            PartCategory.Engine => "Engines",
            PartCategory.Coupling => "Coupling",
            PartCategory.Aerodynamic => "Aero",
            PartCategory.Ground => "Ground",
            PartCategory.Electrical => "Electrical",
            PartCategory.Communication => "Comms",
            _ => c.ToString(),
        };

        public void ShowHint(string text)
        {
            _transientHint = text;
            _hintTimer = 4;
        }

        public override void _Process(double delta)
        {
            _hintTimer -= delta;
            _hint.Text = _hintTimer > 0 ? _transientHint : _editor.HintText;
            _hint.AddThemeColorOverride("font_color", _hintTimer > 0 ? Ui.Warn : Ui.Text);
        }

        // ---- Catalogue -----------------------------------------------------------------------------------------

        private void RefreshCatalog()
        {
            foreach (Node n in _partList.GetChildren()) n.QueueFree();
            foreach (PartDefinition p in Game.Catalog.Search(_search?.Text, _category))
            {
                bool available = Game.IsPartAvailable(p);
                Button b = Ui.Button("", null);
                b.Text = $"{p.Name}";
                b.Alignment = HorizontalAlignment.Left;
                b.TooltipText = $"{p.Description}\n{KeyStat(p)}";
                b.Disabled = !available;
                if (!available) b.Text += "  (locked)";
                b.SizeFlagsHorizontal = SizeFlags.ExpandFill;
                PartDefinition def = p;
                b.Pressed += () => { _editor.StartPlacing(def); ShowInfo(def); };
                b.MouseEntered += () => ShowInfo(def);
                var row = Ui.HBox(4);
                row.AddChild(b);
                row.AddChild(Ui.Label(KeyStat(p), 11, Ui.TextDim));
                _partList.AddChild(row);
            }
        }

        private static string KeyStat(PartDefinition p)
        {
            if (p.GetModule<EngineModuleDef>() is { } e) return $"{e.ThrustVacuum / 1000:0} kN · {e.IspVacuum:0}s";
            if (p.Resources.Count > 0 && p.Resources.All(r => ResourceLibrary.Get(r.Resource).Density > 0)) return $"{p.WetMass / 1000:0.00} t";
            return $"{p.Mass:0} kg";
        }

        private void ShowInfo(PartDefinition p)
        {
            var sb = new StringBuilder();
            sb.Append($"[b][color=#52c7fa]{p.Name}[/color][/b]\n[color=#9aa8b5]{p.Manufacturer} · {CategoryName(p.Category)}[/color]\n{p.Description}\n\n");
            sb.Append($"Mass: {p.Mass:N0} kg dry, {p.WetMass:N0} kg full\nCost: {p.Cost:N0}\n");
            sb.Append($"Size: {Assembly.SizeName(p.AttachNodes.FirstOrDefault()?.Size ?? -1)} · {p.Bounds.Height:0.00} m tall\n");
            sb.Append($"Crash tolerance: {p.CrashTolerance:0} m/s · Joint strength: {p.BreakingForce / 1000:N0} kN\n");
            foreach (ResourceAmountDef r in p.Resources) sb.Append($"{ResourceLibrary.Get(r.Resource).Name}: {r.Max:N0} {ResourceLibrary.Get(r.Resource).Unit}\n");
            foreach (PartModuleDef m in p.Modules)
            {
                switch (m)
                {
                    case EngineModuleDef e:
                        sb.Append($"Thrust: {e.ThrustVacuum / 1000:0.0} kN vac / {e.ThrustAt(1) / 1000:0.0} kN sea level\nIsp: {e.IspVacuum:0} s vac / {e.IspSeaLevel:0} s sea level\nFuel flow: {e.MaxMassFlow:0.00} kg/s\n");
                        if (e.GimbalRange > 0) sb.Append($"Gimbal: ±{e.GimbalRange:0.#}°\n");
                        if (e.IsSolid) sb.Append("Solid motor: cannot be throttled or shut down\n");
                        break;
                    case CommandModuleDef c: sb.Append(c.IsProbe ? $"Probe core ({c.ElectricUsage:0.###} EC/s)\n" : $"Crew: {c.CrewCapacity}\n"); break;
                    case ReactionWheelModuleDef rw: sb.Append($"Reaction wheel: {rw.Torque / 1000:0.0} kN·m\n"); break;
                    case DecouplerModuleDef d: sb.Append($"Decoupler: {d.EjectionImpulse:0} N·s ejection{(d.IsRadial ? " (radial)" : "")}\n"); break;
                    case ParachuteModuleDef ch: sb.Append($"Parachute: drag area {ch.DeployedDragArea:0} m², opens below {ch.DeployAltitude:0} m\n"); break;
                    case SolarPanelModuleDef sp: sb.Append($"Solar: {sp.PowerOutput:0.00} EC/s at Aster{(sp.Deployable ? " (deployable)" : "")}\n"); break;
                    case LandingLegModuleDef: sb.Append("Landing leg with suspension\n"); break;
                    case RcsModuleDef rcs: sb.Append($"RCS: {rcs.Nozzles.Count} × {rcs.ThrustPerNozzle / 1000:0.0} kN, Isp {rcs.Isp:0} s\n"); break;
                    case LiftingSurfaceModuleDef w: sb.Append($"Lifting surface: {w.Area:0.00} m²{(w.IsControlSurface ? ", control surface" : "")}\n"); break;
                    case ExperimentModuleDef ex: sb.Append($"Experiment: {ScienceRules.Get(ex.ExperimentId)?.Name}\n"); break;
                    case AntennaModuleDef a: sb.Append($"Antenna range: {UI.Ui.Distance(a.Range)}\n"); break;
                    case DockingPortModuleDef: sb.Append("Docking port\n"); break;
                    case FuelLineModuleDef: sb.Append("Fuel duct: point it at another stack\n"); break;
                    case JettisonModuleDef j: sb.Append($"Jettisons {j.JettisonMass:0} kg shell when staged\n"); break;
                }
            }
            if (!Game.IsPartAvailable(p)) sb.Append($"\n[color=#ff6655]Requires research: {Game.TechTree.Get(p.Tech)?.Name ?? p.Tech}[/color]");
            _partInfo.Text = sb.ToString();
        }

        // ---- Refresh -------------------------------------------------------------------------------------------

        public void Refresh()
        {
            VesselDesign d = _editor.Design;
            if (!_name.HasFocus()) _name.Text = d.Name;
            _symLabel.Text = $"×{_editor.Symmetry}";
            _undo.Disabled = !_editor.CanUndo;
            _redo.Disabled = !_editor.CanRedo;
            _snap.SetPressedNoSignal(_editor.AngleSnap);
            _centers.SetPressedNoSignal(_editor.ShowCenters);
            _funds.Text = Game.Progress.Mode == GameMode.Career ? $"Funds {Game.Progress.Funds:N0}" : (Game.Progress.Mode == GameMode.Science ? $"Science {Game.Progress.Science:0.0}" : "");
            RefreshStats();
            RefreshStaging();
            RefreshTree();
        }

        private void RefreshStats()
        {
            foreach (Node n in _stats.GetChildren()) n.QueueFree();
            VesselDesign d = _editor.Design;
            if (d.Parts.Count == 0)
            {
                _stats.AddChild(Ui.Label("No parts yet. Start with a command pod.", 13, Ui.TextDim));
                return;
            }
            MassProperties mp = MassProperties.Compute(d.Parts);
            double dry = d.Parts.Sum(p => p.DryMass);
            double g = Game.SolarSystem.HomeBody.SurfaceGravity;
            List<StageStats> stages = DeltaVCalculator.Compute(d, g);
            void Row(string a, string b, Color? c = null)
            {
                Label l = Ui.Row(_stats, a, 130);
                l.Text = b;
                if (c.HasValue) l.AddThemeColorOverride("font_color", c.Value);
            }
            _stats.AddChild(Ui.Heading("VESSEL"));
            Row("Parts", $"{d.Parts.Count}");
            Row("Mass", $"{Ui.Mass(mp.Mass)} ({Ui.Mass(dry)} dry)");
            Row("Cost", $"{d.TotalCost():N0}");
            Row("Height", $"{d.Parts.Max(p => p.LocalToVessel(p.Def.Bounds.Max).Y) - d.Parts.Min(p => p.LocalToVessel(p.Def.Bounds.Min).Y):0.0} m");
            double totalVac = DeltaVCalculator.TotalVacuum(stages), totalSl = DeltaVCalculator.TotalSeaLevel(stages);
            Row("Total Δv (vac)", $"{totalVac:N0} m/s", Ui.Good);
            Row("Total Δv (sea lvl)", $"{totalSl:N0} m/s");
            StageStats first = stages.FirstOrDefault(s => s.ThrustSeaLevel > 0);
            if (first != null) Row("Launch TWR", $"{first.TwrSeaLevel:0.00}", first.TwrSeaLevel < 1.05 ? Ui.Bad : (first.TwrSeaLevel < 1.25 ? Ui.Warn : Ui.Good));
            Row("Orbit needs", "≈ 3,400 m/s from Aster", Ui.TextDim);
            _stats.AddChild(Ui.Heading("STAGES"));
            foreach (StageStats s in stages)
            {
                if (s.DeltaVVacuum < 0.5 && s.ThrustVacuum <= 0) continue;
                Label l = Ui.Label($"Stage {s.Stage + 1}: {s.DeltaVVacuum:N0} m/s vac · {s.DeltaVSeaLevel:N0} sl\n   TWR {s.TwrSeaLevel:0.00} sl / {s.TwrVacuum:0.00} vac · burn {Ui.Duration(s.BurnTime)}\n   {Ui.Mass(s.StartMass)} → {Ui.Mass(s.EndMass)}", 12);
                _stats.AddChild(l);
            }
            // Warnings that help new players.
            var warn = new List<string>();
            if (!d.Parts.Any(p => p.Def.HasModule<CommandModuleDef>())) warn.Add("No command pod or probe core.");
            if (!d.Parts.Any(p => p.Def.HasModule<EngineModuleDef>())) warn.Add("No engines.");
            if (d.Parts.Any(p => p.Def.HasModule<CommandModuleDef>() && p.Def.GetModule<CommandModuleDef>().CrewCapacity > 0) && !d.Parts.Any(p => p.Def.HasModule<ParachuteModuleDef>())) warn.Add("Crewed vessel without a parachute.");
            if (d.Parts.Any(p => p.Def.HasModule<FuelLineModuleDef>() && p.FuelLineTarget < 0)) warn.Add("A fuel duct has no target.");
            if (first != null && first.TwrSeaLevel < 1) warn.Add("First stage cannot lift off (TWR < 1).");
            foreach (string w in warn) _stats.AddChild(Ui.Label("⚠ " + w, 12, Ui.Warn));
        }

        private void RefreshStaging()
        {
            foreach (Node n in _staging.GetChildren()) n.QueueFree();
            VesselDesign d = _editor.Design;
            List<List<PartInstance>> stages = Staging.Stages(d.Parts);
            if (stages.Count == 0) _staging.AddChild(Ui.Label("No stageable parts (engines, decouplers, parachutes, fairings).", 12, Ui.TextDim));
            for (int s = stages.Count - 1; s >= 0; s--)
            {
                _staging.AddChild(Ui.Label($"Stage {s + 1}{(s == 0 ? "  (fires first)" : "")}", 13, Ui.Accent));
                foreach (var grp in stages[s].GroupBy(p => p.SymmetryGroup == 0 ? -p.Id : p.SymmetryGroup))
                {
                    PartInstance p = grp.First();
                    HBoxContainer row = Ui.HBox(4);
                    Label l = Ui.Label($"{grp.Count()}× {p.Def.Name}", 12);
                    l.SizeFlagsHorizontal = SizeFlags.ExpandFill;
                    row.AddChild(l);
                    row.AddChild(Ui.Button("▲", () => _editor.ChangeStage(p, +1), "Fire in a later stage"));
                    row.AddChild(Ui.Button("▼", () => _editor.ChangeStage(p, -1), "Fire in an earlier stage"));
                    _staging.AddChild(row);
                }
            }
        }

        private void RefreshTree()
        {
            _tree.Clear();
            VesselDesign d = _editor.Design;
            PartInstance root = d.Root;
            if (root == null) return;
            void Add(PartInstance p, TreeItem parent)
            {
                TreeItem it = _tree.CreateItem(parent);
                it.SetText(0, $"{p.Def?.Name ?? p.Part}{(p.Stage >= 0 ? $"  [S{p.Stage + 1}]" : "")}{(p.IsSurfaceAttached ? "  (radial)" : "")}");
                it.SetMetadata(0, p.Id);
                foreach (PartInstance c in d.Children(p.Id)) Add(c, it);
            }
            Add(root, null);
        }

        // ---- Dialogs ---------------------------------------------------------------------------------------

        private void ShowDialog(Control content)
        {
            CloseDialog();
            var dim = new ColorRect { Color = new Color(0, 0, 0, 0.5f), MouseFilter = MouseFilterEnum.Stop };
            Ui.FullRect(dim);
            var center = new CenterContainer();
            Ui.FullRect(center);
            center.AddChild(content);
            dim.AddChild(center);
            _dialog = dim;
            AddChild(dim);
        }

        private void CloseDialog()
        {
            _dialog?.QueueFree();
            _dialog = null;
        }

        public void SaveDesign()
        {
            _editor.Rename(_name.Text);
            bool ok = Game.SaveDesign(_editor.Design, out string path);
            ShowHint(ok ? $"Saved to {ProjectSettings.GlobalizePath(path)}" : "Saving failed.");
        }

        private void ShowLoad()
        {
            var box = Ui.VBox(6);
            box.AddChild(Ui.Label("Vessels", 20, Ui.Accent));
            var scroll = new ScrollContainer { CustomMinimumSize = new Vector2(560, 360) };
            var list = Ui.VBox(4);
            list.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            scroll.AddChild(list);
            box.AddChild(scroll);
            Label status = Ui.Label("", 12, Ui.Warn);
            foreach (string file in Game.ListVesselFiles())
            {
                string f = file;
                HBoxContainer row = Ui.HBox(4);
                Label l = Ui.Label(f.Replace(".osp-vessel.json", "").Replace('_', ' '), 13);
                l.SizeFlagsHorizontal = SizeFlags.ExpandFill;
                row.AddChild(l);
                row.AddChild(Ui.Button("Load", () =>
                {
                    try
                    {
                        VesselDesign d = Game.LoadDesign(f, out List<string> warnings);
                        _editor.SetDesign(d);
                        CloseDialog();
                        if (warnings.Count > 0) ShowHint(string.Join(" ", warnings));
                    }
                    catch (System.Exception ex) when (ex is System.FormatException or System.InvalidOperationException)
                    {
                        status.Text = ex.Message;
                    }
                }));
                row.AddChild(Ui.Button("Duplicate", () =>
                {
                    try
                    {
                        VesselDesign d = Game.LoadDesign(f, out _);
                        d.Name += " Copy";
                        Game.SaveDesign(d, out _);
                        ShowLoad();
                    }
                    catch (System.Exception ex) when (ex is System.FormatException or System.InvalidOperationException) { status.Text = ex.Message; }
                }));
                row.AddChild(Ui.Button("Delete", () => { Game.DeleteDesign(f); ShowLoad(); }));
                list.AddChild(row);
            }
            box.AddChild(status);
            box.AddChild(Ui.Label($"Folder: {ProjectSettings.GlobalizePath(GameRoot.VesselDir)}", 11, Ui.TextDim));
            box.AddChild(Ui.Button("Close", CloseDialog));
            ShowDialog(Ui.Panel(box));
        }

        private void ShowMenu()
        {
            var box = Ui.VBox(8);
            box.AddChild(Ui.Label("Game", 20, Ui.Accent));
            var name = new LineEdit { Text = Game.CurrentSaveName, CustomMinimumSize = new Vector2(300, 0) };
            box.AddChild(name);
            box.AddChild(Ui.Button("Save game", () =>
            {
                string n = string.Concat(name.Text.Where(c => char.IsLetterOrDigit(c) || c == '-' || c == '_'));
                bool ok = Game.SaveGame(n.Length == 0 ? "save" : n);
                ShowHint(ok ? "Game saved." : "Saving failed.");
                CloseDialog();
            }));
            box.AddChild(Ui.Button("Settings…", () => ShowDialog(Menu.SettingsPanel.Create(CloseDialog))));
            box.AddChild(Ui.Button("Main menu", () => Game.GoToMenu()));
            box.AddChild(Ui.Button("Close", CloseDialog));
            ShowDialog(Ui.Panel(box));
        }

        private void ShowTracking()
        {
            var box = Ui.VBox(6);
            box.AddChild(Ui.Label("Tracking station", 20, Ui.Accent));
            FlightWorld w = Game.World;
            if (w == null || w.Vessels.Count == 0) box.AddChild(Ui.Label("No vessels in flight.", 13, Ui.TextDim));
            else
            {
                foreach (Vessel v in w.Vessels.OrderBy(v => v.Type))
                {
                    Vessel vessel = v;
                    HBoxContainer row = Ui.HBox(6);
                    Label l = Ui.Label($"{v.Name} — {v.Situation} at {v.Body.Name}{(v.Type == VesselType.Debris ? " (debris)" : "")}", 13);
                    l.SizeFlagsHorizontal = SizeFlags.ExpandFill;
                    row.AddChild(l);
                    row.AddChild(Ui.Button("Fly", () => { w.SetActive(vessel); Game.ResumeFlight(); }));
                    row.AddChild(Ui.Button("Terminate", () => { w.RemoveVessel(vessel); ShowTracking(); }));
                    box.AddChild(row);
                }
            }
            box.AddChild(Ui.Button("Close", CloseDialog));
            var p = Ui.Panel(box);
            p.CustomMinimumSize = new Vector2(560, 0);
            ShowDialog(p);
        }

        private void ShowResearch()
        {
            var box = Ui.VBox(6);
            ProgressState prog = Game.Progress;
            box.AddChild(Ui.Label($"Research — {prog.Science:0.0} science available", 20, Ui.Accent));
            var scroll = new ScrollContainer { CustomMinimumSize = new Vector2(640, 420) };
            var list = Ui.VBox(6);
            list.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            scroll.AddChild(list);
            foreach (TechNode n in Game.TechTree.Nodes)
            {
                bool have = prog.UnlockedTech.Contains(n.Id);
                bool can = prog.CanResearch(Game.TechTree, n, out string reason);
                var row = Ui.HBox(6);
                var info = Ui.VBox(1);
                info.SizeFlagsHorizontal = SizeFlags.ExpandFill;
                info.AddChild(Ui.Label($"{n.Name} ({n.Cost:0} science)", 14, have ? Ui.Good : Ui.Text));
                string parts = string.Join(", ", Game.Catalog.All.Where(p => p.Tech == n.Id).Select(p => p.Name));
                info.AddChild(Ui.Label($"{n.Description}\nUnlocks: {(parts.Length > 0 ? parts : "—")}", 11, Ui.TextDim));
                row.AddChild(info);
                string id = n.Id;
                Button b = Ui.Button(have ? "Researched" : "Research", () =>
                {
                    if (prog.Research(Game.TechTree, id)) { RefreshCatalog(); Refresh(); ShowResearch(); }
                }, have ? "" : reason);
                b.Disabled = have || !can;
                row.AddChild(b);
                list.AddChild(row);
            }
            box.AddChild(scroll);
            box.AddChild(Ui.Button("Close", CloseDialog));
            ShowDialog(Ui.Panel(box));
        }

        private void ShowContracts()
        {
            var box = Ui.VBox(6);
            ProgressState prog = Game.Progress;
            box.AddChild(Ui.Label("Contracts", 20, Ui.Accent));
            box.AddChild(Ui.Heading("Active"));
            foreach (string id in prog.AcceptedContracts)
            {
                ContractDefinition c = Game.Contracts.Contracts.FirstOrDefault(x => x.Id == id);
                if (c != null) box.AddChild(Ui.Label($"• {c.Title}: {c.Description}  (reward {c.RewardFunds:N0})", 13));
            }
            box.AddChild(Ui.Heading("Offered"));
            foreach (ContractDefinition c in prog.OfferedContracts(Game.Contracts))
            {
                ContractDefinition con = c;
                var row = Ui.HBox(6);
                Label l = Ui.Label($"{c.Title}: {c.Description}\n   advance {c.AdvanceFunds:N0} · reward {c.RewardFunds:N0} funds, {c.RewardScience:0} science", 12);
                l.SizeFlagsHorizontal = SizeFlags.ExpandFill;
                row.AddChild(l);
                row.AddChild(Ui.Button("Accept", () => { prog.Accept(con); Refresh(); ShowContracts(); }));
                box.AddChild(row);
            }
            box.AddChild(Ui.Label($"Completed: {prog.CompletedContracts.Count}", 12, Ui.TextDim));
            box.AddChild(Ui.Button("Close", CloseDialog));
            var p = Ui.Panel(box);
            p.CustomMinimumSize = new Vector2(620, 0);
            ShowDialog(p);
        }

        public void ToggleHelp()
        {
            if (_help != null && IsInstanceValid(_help))
            {
                _help.QueueFree();
                _help = null;
                return;
            }
            var box = Ui.VBox(4);
            box.AddChild(Ui.Label("Vehicle assembly", 20, Ui.Accent));
            box.AddChild(Ui.Label(
                "• Click a part in the catalogue, then move it onto a green node or a surface and click to attach.\n" +
                "• The first part becomes the root (usually a command pod). Build downwards from it.\n" +
                "• Click an attached part to pick it up with everything below it. Alt+click duplicates. Del deletes.\n" +
                "• R / F rotate the held part (Shift for fine steps). X cycles radial symmetry. C toggles angle snap.\n" +
                "• Hold Shift when clicking to keep placing the same part.\n" +
                "• Right-drag orbits the camera, middle-drag (or Shift+right-drag) pans, wheel zooms, PgUp/PgDn move up/down.\n" +
                "• Ctrl+Z / Ctrl+Y undo and redo. Ctrl+S saves.\n" +
                "• Staging tab: stage 1 fires first. Use ▲/▼ to move parts between stages.\n" +
                "• Radial decouplers go on the side of a tank; attach boosters to the decoupler's outer face.", 13));
            box.AddChild(Ui.Button("Close", ToggleHelp));
            var center = new CenterContainer();
            Ui.FullRect(center);
            center.AddChild(Ui.Panel(box));
            _help = center;
            AddChild(center);
        }
    }
}
