using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Godot;
using OSP.Core.Parts;
using OSP.Core.Save;
using OSP.Core.Util;
using OSP.Core.Vehicles;
using OSP.Game.Data;
using OSP.Game.UI;

namespace OSP.Game.Editor;

public sealed partial class EditorScene
{
    private CanvasLayer _ui = null!;
    private Control _uiRoot = null!;
    private LineEdit _nameEdit = null!, _search = null!;
    private OptionButton _category = null!;
    private ItemList _partList = null!;
    private Label _partInfo = null!, _hint = null!, _stats = null!, _issues = null!, _symLabel = null!, _selInfo = null!;
    private Tree _tree = null!;
    private VBoxContainer _stageBox = null!;
    private Button _undoBtn = null!, _redoBtn = null!, _launchBtn = null!;
    private readonly List<PartDefinition> _listDefs = new();
    private bool _treeSyncing;

    private void BuildUi()
    {
        _ui = new CanvasLayer(); AddChild(_ui);
        _uiRoot = new Control { Theme = Ui.Theme, MouseFilter = Control.MouseFilterEnum.Ignore };
        _uiRoot.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _ui.AddChild(_uiRoot);

        // ---------- top bar ----------
        var top = Ui.HBox(6);
        top.AddChild(Ui.Label("VEHICLE ASSEMBLY", 18, Ui.Accent));
        _nameEdit = new LineEdit { Text = _design.Name, CustomMinimumSize = new Vector2(230, 0), PlaceholderText = "Vehicle name" };
        _nameEdit.TextChanged += t => _design.Name = t;
        top.AddChild(_nameEdit);
        top.AddChild(Ui.Button("New", NewDesign, "Start an empty vehicle"));
        top.AddChild(Ui.Button("Save", SaveDesign, "Save this design"));
        top.AddChild(Ui.Button("Load…", ShowLoad, "Open a saved or example design"));
        _undoBtn = Ui.Button("Undo", DoUndo, "Ctrl+Z"); _redoBtn = Ui.Button("Redo", DoRedo, "Ctrl+Y");
        top.AddChild(_undoBtn); top.AddChild(_redoBtn);
        _symLabel = Ui.Label("", 15, Ui.Warn);
        var symBtn = Ui.Button("Symmetry", CycleSymmetry, "X — number of copies for radial parts");
        top.AddChild(symBtn); top.AddChild(_symLabel);
        top.AddChild(Ui.Filler());
        top.AddChild(Ui.Button("Frame", FrameVehicle, "Home"));
        _launchBtn = Ui.Button("LAUNCH ▶", Launch, "Move the vehicle to the launch pad", 130);
        top.AddChild(_launchBtn);
        top.AddChild(Ui.Button("Menu", () => { GameState.Instance.CurrentDesign = _design.Clone(); GameState.Instance.GoTo("MainMenu"); }));
        var topPanel = Ui.Panel(top, 4);
        topPanel.SetAnchorsPreset(Control.LayoutPreset.TopWide);
        topPanel.OffsetLeft = 8; topPanel.OffsetRight = -8; topPanel.OffsetTop = 8;
        _uiRoot.AddChild(topPanel);

        // ---------- left: catalogue ----------
        var left = Ui.VBox(6);
        left.AddChild(Ui.Label("PARTS", 16, Ui.Accent));
        _category = new OptionButton();
        _category.AddItem("All categories");
        foreach (var c in _cat.Categories.OrderBy(c => c)) _category.AddItem(c);
        _category.ItemSelected += _ => RebuildList();
        left.AddChild(_category);
        _search = new LineEdit { PlaceholderText = "Search parts…", ClearButtonEnabled = true };
        _search.TextChanged += _ => RebuildList();
        left.AddChild(_search);
        _partList = new ItemList { SizeFlagsVertical = Control.SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(300, 260), SelectMode = ItemList.SelectModeEnum.Single };
        _partList.ItemSelected += i => { if (i >= 0 && i < _listDefs.Count) { ShowPartInfo(_listDefs[(int)i]); } };
        _partList.ItemActivated += i => PickCatalogPart(_listDefs[(int)i]);
        _partList.ItemClicked += (i, _, btn) => { if (btn == (long)MouseButton.Left) { ShowPartInfo(_listDefs[(int)i]); PickCatalogPart(_listDefs[(int)i]); } };
        left.AddChild(_partList);
        _partInfo = Ui.Label("Click a part, then click the vehicle to attach it.", 13, Ui.Dim, true);
        _partInfo.CustomMinimumSize = new Vector2(300, 120);
        left.AddChild(_partInfo);
        var leftPanel = Ui.Panel(left, 6);
        leftPanel.SetAnchorsPreset(Control.LayoutPreset.LeftWide);
        leftPanel.OffsetLeft = 8; leftPanel.OffsetTop = 64; leftPanel.OffsetBottom = -44; leftPanel.CustomMinimumSize = new Vector2(330, 0);
        leftPanel.OffsetRight = 346;
        _uiRoot.AddChild(leftPanel);
        RebuildList();

        // ---------- right: stats, tree, staging ----------
        var right = Ui.VBox(6);
        right.AddChild(Ui.Label("VEHICLE", 16, Ui.Accent));
        _stats = Ui.Label("", 14, Ui.Text);
        right.AddChild(_stats);
        right.AddChild(Ui.Sep());
        right.AddChild(Ui.Label("Hierarchy", 15, Ui.Accent));
        _tree = new Tree { CustomMinimumSize = new Vector2(330, 170), HideRoot = true, SelectMode = Tree.SelectModeEnum.Single };
        _tree.ItemSelected += OnTreeSelected;
        right.AddChild(_tree);
        _selInfo = Ui.Label("", 13, Ui.Dim, true);
        right.AddChild(_selInfo);
        var selRow = Ui.HBox(4);
        selRow.AddChild(Ui.Button("Delete", DeleteSelected, "Delete key"));
        selRow.AddChild(Ui.Button("Duplicate", () => DuplicateSelected(false), "Ctrl+D"));
        selRow.AddChild(Ui.Button("Move", () => DuplicateSelected(true), "M"));
        right.AddChild(selRow);
        right.AddChild(Ui.Sep());
        right.AddChild(Ui.Label("Staging (fires from the highest stage down)", 15, Ui.Accent));
        _stageBox = Ui.VBox(2);
        right.AddChild(_stageBox);
        var stRow = Ui.HBox(4);
        stRow.AddChild(Ui.Button("Stage −", () => ShiftStage(-1), "Fire the selected part earlier/later"));
        stRow.AddChild(Ui.Button("Stage +", () => ShiftStage(+1)));
        stRow.AddChild(Ui.Button("Auto-stage", () => { PushUndo(); StagingPlanner.AutoAssign(_design, _cat); RefreshUi(); }));
        right.AddChild(stRow);
        _issues = Ui.Label("", 13, Ui.Warn, true);
        right.AddChild(_issues);
        var rightScroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        rightScroll.AddChild(right);
        var rightPanel = Ui.Panel(rightScroll, 6);
        rightPanel.SetAnchorsPreset(Control.LayoutPreset.RightWide);
        rightPanel.OffsetLeft = -380; rightPanel.OffsetRight = -8; rightPanel.OffsetTop = 64; rightPanel.OffsetBottom = -44;
        _uiRoot.AddChild(rightPanel);

        // ---------- bottom hint ----------
        _hint = Ui.Label("", 15, Ui.Text);
        var hp = Ui.Panel(_hint, 4);
        hp.SetAnchorsPreset(Control.LayoutPreset.BottomWide);
        hp.OffsetLeft = 360; hp.OffsetRight = -394; hp.OffsetBottom = -8; hp.OffsetTop = -40;
        _uiRoot.AddChild(hp);
        SetHint("Pick a part from the list. The first part you place becomes the root (usually a command pod).");
    }

    private void SetHint(string t) { if (_hint != null) _hint.Text = t; }

    // ---------------------------------------------------------------------------------------------- catalogue
    private void RebuildList()
    {
        _partList.Clear(); _listDefs.Clear();
        string? cat = _category.Selected > 0 ? _category.GetItemText(_category.Selected) : null;
        string q = _search.Text.Trim().ToLowerInvariant();
        foreach (var p in _cat.All.OrderBy(p => p.Category).ThenBy(p => p.DryMass))
        {
            if (cat != null && p.Category != cat) continue;
            if (q.Length > 0 && !(p.Name.ToLowerInvariant().Contains(q) || p.Description.ToLowerInvariant().Contains(q) || p.Category.ToLowerInvariant().Contains(q))) continue;
            _partList.AddItem($"{p.Name}   · {CatalogMass(p)}");
            _partList.SetItemTooltip(_partList.ItemCount - 1, p.Description);
            _listDefs.Add(p);
        }
    }

    private static string CatalogMass(PartDefinition p) => Ui.FormatMass(PartCatalog.WetMass(p));

    private void ShowPartInfo(PartDefinition p)
    {
        var sb = new StringBuilder();
        sb.AppendLine(p.Name + "  [" + p.Category + "]");
        sb.AppendLine(p.Description);
        sb.AppendLine($"Mass {Ui.FormatMass(p.DryMass)} dry / {Ui.FormatMass(PartCatalog.WetMass(p))} wet · Cost {p.Cost:F0}");
        if (p.Engine is { } e)
            sb.AppendLine($"Thrust {e.ThrustVac / 1000:F0} kN vac / {e.Thrust(1, 1) / 1000:F0} kN ASL · Isp {e.IspVac:F0} s vac / {e.IspAtm:F0} s ASL" + (e.GimbalDeg > 0 ? $" · gimbal ±{e.GimbalDeg:F0}°" : "") + (e.Solid ? " · solid (no throttle)" : ""));
        foreach (var r in p.Resources) sb.AppendLine($"{r.Name}: {r.Capacity:F0}");
        if (p.Decoupler != null) sb.AppendLine($"Separation impulse {p.Decoupler.SeparationImpulse:F0} N·s");
        if (p.Command != null) sb.AppendLine(p.Command.Probe ? "Probe core (uses electric charge)" : $"Crew capacity {p.Command.CrewCapacity}");
        if (p.ReactionWheel != null) sb.AppendLine($"Reaction wheel {p.ReactionWheel.Torque / 1000:F1} kN·m");
        if (p.Solar != null) sb.AppendLine($"Solar {p.Solar.ChargeRate:F1} EC/s at 1 AU-equivalent");
        if (p.Parachute != null) sb.AppendLine($"Parachute Cd·A {p.Parachute.FullDragArea:F0} m²");
        if (p.Leg != null) sb.AppendLine("Retractable landing leg");
        if (p.AeroSurface != null) sb.AppendLine(p.AeroSurface.Control ? "Control surface" : "Fixed stabiliser fin");
        if (p.Rcs != null) sb.AppendLine($"RCS: {p.Rcs.Nozzles.Count} nozzles, Isp {p.Rcs.Isp:F0} s");
        _partInfo.Text = sb.ToString();
    }

    // ---------------------------------------------------------------------------------------------- refresh
    private void RefreshUi()
    {
        if (_ui == null) return;
        RefreshTopBar();
        RefreshStats();
        RefreshTree();
        RefreshStaging();
        RefreshSelectionUi();
    }

    private void RefreshTopBar()
    {
        _symLabel.Text = $"×{_symmetry}";
        _undoBtn.Disabled = _undo.Count == 0; _redoBtn.Disabled = _redo.Count == 0;
        if (_nameEdit.Text != _design.Name && !_nameEdit.HasFocus()) _nameEdit.Text = _design.Name;
    }

    private void RefreshStats()
    {
        if (_design.IsEmpty)
        {
            _stats.Text = "Empty vehicle.\nPick a command pod or probe core to start.";
            _issues.Text = ""; _launchBtn.Disabled = true;
            return;
        }
        var home = GameData.System.Home;
        var st = VehicleAnalysis.Analyze(_design, _cat, home.SurfaceGravity, home.Atmosphere?.SeaLevelPressureAtm ?? 0);
        var sb = new StringBuilder();
        sb.AppendLine($"Parts: {st.PartCount}    Cost: {st.Cost:F0}    Crew: {st.Crew}");
        sb.AppendLine($"Mass: {Ui.FormatMass(st.WetMass)} wet / {Ui.FormatMass(st.DryMass)} dry");
        sb.AppendLine($"Launch thrust: {st.LaunchThrustAsl / 1000:F0} kN (ASL)   {st.LaunchThrustVac / 1000:F0} kN (vac)");
        sb.AppendLine($"TWR on {home.Name}: {st.LaunchTwr:F2}" + (st.LaunchTwr < 1 && st.LaunchThrustAsl > 0 ? "  (cannot lift off!)" : ""));
        sb.AppendLine($"Δv: {st.DeltaVVacuum:F0} m/s vacuum · {st.DeltaVAtmosphere:F0} m/s sea level");
        foreach (var s in st.StagesVacuum) sb.AppendLine($"  stage {s.Stage}: Δv {s.DeltaV:F0}  burn {s.BurnTime:F0}s  TWR {s.Twr:F2}");
        if (st.MonopropMass > 0 || st.ElectricCapacity > 0) sb.AppendLine($"Monoprop {st.MonopropMass:F0} kg · Electric {st.ElectricCapacity:F0} EC");
        _stats.Text = sb.ToString();

        var issues = VehicleValidator.Validate(_design, _cat);
        _issues.Text = string.Join("\n", issues.Select(i => (i.Severity == IssueSeverity.Error ? "✖ " : "⚠ ") + i.Message));
        _issues.AddThemeColorOverride("font_color", issues.Any(i => i.Severity == IssueSeverity.Error) ? Ui.Bad : Ui.Warn);
        _launchBtn.Disabled = issues.Any(i => i.Severity == IssueSeverity.Error);
    }

    private void RefreshTree()
    {
        _treeSyncing = true;
        _tree.Clear();
        var rootItem = _tree.CreateItem();
        var items = new Dictionary<int, TreeItem>();
        void Add(PartInstance p, TreeItem parent)
        {
            var def = _cat.Get(p.PartId);
            var it = _tree.CreateItem(parent);
            it.SetText(0, def.Name + (p.Stage >= 0 && def.CanStage ? $"   [stage {p.Stage}]" : "") + (p.SymmetryGroup != 0 ? "  ⟲" : ""));
            it.SetMetadata(0, p.Id);
            items[p.Id] = it;
            foreach (var c in _design.ChildrenOf(p.Id).OrderBy(c => c.Id)) Add(c, it);
        }
        if (_design.Root != null) Add(_design.Root, rootItem);
        if (_selected >= 0 && items.TryGetValue(_selected, out var sel)) sel.Select(0);
        _treeSyncing = false;
    }

    private void OnTreeSelected()
    {
        if (_treeSyncing) return;
        var it = _tree.GetSelected(); if (it == null) return;
        _selected = it.GetMetadata(0).AsInt32();
        ApplySelectionHighlight(); RefreshSelectionUi();
    }

    private void RefreshSelectionUi()
    {
        if (_selInfo == null) return;
        var p = _selected >= 0 ? _design.Find(_selected) : null;
        if (p == null) { _selInfo.Text = "Select a part (click it or pick from the hierarchy)."; return; }
        var d = _cat.Get(p.PartId);
        _selInfo.Text = $"{d.Name} · {Ui.FormatMass(PartCatalog.WetMass(d))}" + (d.CanStage ? $" · stage {p.Stage}" : "");
    }

    private void RefreshStaging()
    {
        foreach (var c in _stageBox.GetChildren()) c.QueueFree();
        if (_design.IsEmpty) return;
        var groups = _design.Parts.Where(p => p.Stage >= 0).GroupBy(p => p.Stage).OrderByDescending(g => g.Key).ToList();
        if (groups.Count == 0) { _stageBox.AddChild(Ui.Label("No staged parts (no engines or decouplers).", 13, Ui.Dim)); return; }
        foreach (var g in groups)
        {
            var names = g.GroupBy(p => _cat.Get(p.PartId).Name).Select(x => x.Count() > 1 ? $"{x.Key} ×{x.Count()}" : x.Key);
            _stageBox.AddChild(Ui.Label($"Stage {g.Key}: {string.Join(", ", names)}", 13, g.Key == groups[0].Key ? Ui.Warn : Ui.Text, true));
        }
    }

    private void ShiftStage(int delta)
    {
        var p = _selected >= 0 ? _design.Find(_selected) : null;
        if (p == null || !_cat.Get(p.PartId).CanStage) { SetHint("Select an engine or decoupler first."); return; }
        PushUndo();
        int ns = Math.Max(0, p.Stage + delta);
        var targets = p.SymmetryGroup != 0 ? _design.Parts.Where(q => q.SymmetryGroup == p.SymmetryGroup).ToList() : new List<PartInstance> { p };
        foreach (var t in targets) t.Stage = ns;
        RefreshUi();
    }

    // ---------------------------------------------------------------------------------------------- file actions
    private void NewDesign()
    {
        PushUndo();
        CancelHeld(true);
        _design = new VehicleDesign { Name = "Untitled Vehicle" };
        _selected = -1;
        RefreshAll(true);
    }

    private void SaveDesign()
    {
        if (_design.IsEmpty) { SetHint("Nothing to save."); return; }
        try
        {
            var file = GameState.Instance.SaveDesign(_design);
            GameState.Instance.CurrentDesign = _design.Clone();
            SetHint($"Saved '{_design.Name}' to {GameState.DesignDir}/{file}.osp-vehicle.json");
        }
        catch (Exception ex) { SetHint("Save failed: " + ex.Message); }
    }

    private void Launch()
    {
        var issues = VehicleValidator.Validate(_design, _cat);
        if (issues.Any(i => i.Severity == IssueSeverity.Error)) { SetHint("Fix the errors first: " + issues.First(i => i.Severity == IssueSeverity.Error).Message); return; }
        var gs = GameState.Instance;
        gs.CurrentDesign = _design.Clone();
        gs.PendingLaunch = _design.Clone();
        try { gs.SaveDesign(_design); } catch (Exception ex) { GD.PushWarning("Autosave failed: " + ex.Message); }
        gs.GoTo("Flight");
    }

    private void ShowLoad()
    {
        var gs = GameState.Instance;
        var v = Ui.VBox(8);
        var list = new ItemList { CustomMinimumSize = new Vector2(500, 280) };
        var entries = new List<(string name, bool example)>();
        foreach (var n in gs.ListDesigns()) { list.AddItem(n); entries.Add((n, false)); }
        foreach (var n in gs.ListExampleDesigns()) { list.AddItem($"{n.Replace('_', ' ')}   (example)"); entries.Add((n, true)); }
        v.AddChild(list);
        var msg = Ui.Label("", 13, Ui.Warn, true); v.AddChild(msg);
        var rename = new LineEdit { PlaceholderText = "New name for Rename / Duplicate" };
        v.AddChild(rename);
        Modal? modal = null;
        var row = Ui.HBox(6);
        row.AddChild(Ui.Button("Load", () =>
        {
            var sel = list.GetSelectedItems(); if (sel.Length == 0) return;
            try
            {
                var (n, ex) = entries[sel[0]];
                PushUndo(); CancelHeld(true);
                _design = gs.LoadDesign(n, ex); _selected = -1;
                modal?.Close(); RefreshAll(true);
            }
            catch (DataException e) { msg.Text = "Could not load: " + e.Message; }
        }));
        row.AddChild(Ui.Button("Duplicate", () =>
        {
            var sel = list.GetSelectedItems(); if (sel.Length == 0) return;
            try
            {
                var (n, ex) = entries[sel[0]];
                var d = gs.LoadDesign(n, ex);
                d.Name = rename.Text.Trim() != "" ? rename.Text.Trim() : d.Name + " copy";
                gs.SaveDesign(d); msg.Text = $"Created '{d.Name}'.";
            }
            catch (Exception e) { msg.Text = "Failed: " + e.Message; }
        }));
        row.AddChild(Ui.Button("Rename", () =>
        {
            var sel = list.GetSelectedItems(); if (sel.Length == 0 || rename.Text.Trim() == "") { msg.Text = "Select a saved design and type a new name."; return; }
            var (n, ex) = entries[sel[0]]; if (ex) { msg.Text = "Examples cannot be renamed; use Duplicate."; return; }
            try { var d = gs.LoadDesign(n); d.Name = rename.Text.Trim(); gs.SaveDesign(d); if (GameState.SafeName(d.Name) != n) gs.DeleteDesign(n); modal?.Close(); ShowLoad(); }
            catch (Exception e) { msg.Text = "Failed: " + e.Message; }
        }));
        row.AddChild(Ui.Button("Delete", () =>
        {
            var sel = list.GetSelectedItems(); if (sel.Length == 0) return;
            var (n, ex) = entries[sel[0]]; if (ex) { msg.Text = "Examples cannot be deleted."; return; }
            gs.DeleteDesign(n); modal?.Close(); ShowLoad();
        }));
        v.AddChild(row);
        modal = Modal.Show(_ui, "Load vehicle", v, new Vector2(560, 0));
    }
}
