using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using OSP.Core.Celestial;
using OSP.Core.Flight;
using OSP.Core.Parts;
using OSP.Core.Persistence;
using OSP.Core.Science;
using OSP.Core.Vehicles;
using OSP.Game.Editor;
using OSP.Game.Flight;
using OSP.Game.Menu;

namespace OSP.Game.App
{
    /// <summary>
    /// Root node of the game (scenes/Main.tscn). Loads game data, owns the persistent game state and switches
    /// between the main menu, the vehicle editor and the flight scene.
    /// </summary>
    public partial class GameRoot : Node
    {
        public static GameRoot Instance { get; private set; }

        public PartCatalog Catalog { get; private set; }
        public CelestialSystem SolarSystem { get; private set; }
        public TechTree TechTree { get; private set; }
        public ContractList Contracts { get; private set; }
        public GameSettings Settings { get; private set; } = new();
        public ProgressState Progress { get; set; } = new();
        /// <summary>Universal time of the persistent game (advances while flying).</summary>
        public double UniversalTime { get; set; }
        /// <summary>The design currently open in the editor.</summary>
        public VesselDesign CurrentDesign { get; set; }
        /// <summary>The persistent flight world (vessels left in flight survive returning to the editor).</summary>
        public FlightWorld World { get; set; }
        public VesselDesign LaunchDesign { get; set; }
        public string CurrentSaveName { get; set; } = "persistent";
        public List<string> DataErrors { get; } = new();

        public SmokeTest SmokeTest { get; private set; }

        private Node _currentScene;

        public const string VesselDir = "user://vessels";
        public const string SaveDir = "user://saves";
        public const string SettingsPath = "user://settings.json";

        public override void _Ready()
        {
            Instance = this;
            LoadSettings();
            InputBindings.Apply(Settings);
            ApplyDisplaySettings();
            LoadGameData();
            DirAccess.MakeDirRecursiveAbsolute(VesselDir);
            DirAccess.MakeDirRecursiveAbsolute(SaveDir);
            InstallStockVessels();

            string[] args = OS.GetCmdlineUserArgs();
            if (args.Contains("--smoke-test"))
            {
                SmokeTest = new SmokeTest { Name = "SmokeTest" };
                AddChild(SmokeTest);
                SmokeTest.Begin(args);
                return;
            }
            GoToMenu();
        }

        // ----- Data -----------------------------------------------------------------------------------

        private void LoadGameData()
        {
            Catalog = new PartCatalog();
            using (DirAccess dir = DirAccess.Open("res://data/parts"))
            {
                if (dir == null)
                {
                    DataErrors.Add("Missing res://data/parts directory.");
                }
                else
                {
                    foreach (string file in dir.GetFiles().Where(f => f.EndsWith(".json")).OrderBy(f => f))
                        Catalog.LoadJson(ReadText("res://data/parts/" + file), file);
                }
            }
            // Mods: extra part files in user://mods/parts are loaded after the stock catalogue.
            string modDir = "user://mods/parts";
            if (DirAccess.DirExistsAbsolute(modDir))
            {
                using DirAccess mods = DirAccess.Open(modDir);
                foreach (string file in mods.GetFiles().Where(f => f.EndsWith(".json")))
                    Catalog.LoadJson(ReadText(modDir + "/" + file), "mod:" + file);
            }
            DataErrors.AddRange(Catalog.LoadErrors);
            foreach (string e in Catalog.LoadErrors) GD.PushWarning(e);

            SolarSystem = CelestialSystem.LoadFromJson(ReadText("res://data/system.json"));
            TechTree = TechTree.FromJson(ReadText("res://data/techtree.json"));
            Contracts = ContractList.FromJson(ReadText("res://data/contracts.json"));
            GD.Print($"[OSP] Loaded {Catalog.All.Count} parts, {SolarSystem.Bodies.Count} bodies, {TechTree.Nodes.Count} tech nodes, {Contracts.Contracts.Count} contracts.");
        }

        public static string ReadText(string path)
        {
            using FileAccess f = FileAccess.Open(path, FileAccess.ModeFlags.Read);
            if (f == null) throw new InvalidOperationException($"Cannot read {path}: {FileAccess.GetOpenError()}");
            return f.GetAsText();
        }

        public static bool WriteText(string path, string text)
        {
            using FileAccess f = FileAccess.Open(path, FileAccess.ModeFlags.Write);
            if (f == null)
            {
                GD.PushError($"Cannot write {path}: {FileAccess.GetOpenError()}");
                return false;
            }
            f.StoreString(text);
            return true;
        }

        /// <summary>Copies stock vessel blueprints into the user's vessel folder the first time the game runs.</summary>
        private void InstallStockVessels()
        {
            using DirAccess dir = DirAccess.Open("res://data/vessels");
            if (dir == null) return;
            foreach (string file in dir.GetFiles().Where(f => f.EndsWith(".json")))
            {
                string target = VesselDir + "/" + file;
                if (FileAccess.FileExists(target)) continue;
                WriteText(target, ReadText("res://data/vessels/" + file));
            }
        }

        // ----- Settings ---------------------------------------------------------------------------------

        private void LoadSettings()
        {
            if (!FileAccess.FileExists(SettingsPath)) return;
            try
            {
                Settings = GameSettings.FromJson(ReadText(SettingsPath));
            }
            catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException)
            {
                GD.PushWarning("Settings file was invalid and has been reset: " + ex.Message);
                Settings = new GameSettings();
            }
        }

        public void SaveSettings()
        {
            WriteText(SettingsPath, Settings.ToJson());
        }

        public void ApplyDisplaySettings()
        {
            Window w = GetWindow();
            float dpi = DisplayServer.ScreenGetScale();
            w.ContentScaleFactor = (float)(Settings.UiScale * System.Math.Max(1.0f, dpi));
            Viewport vp = GetViewport();
            vp.Scaling3DScale = (float)Settings.RenderScale;
            vp.Msaa3D = Settings.Quality == GraphicsQuality.Low ? Viewport.Msaa.Disabled : (Settings.Quality == GraphicsQuality.Medium ? Viewport.Msaa.Msaa2X : Viewport.Msaa.Msaa4X);
            DisplayServer.WindowSetVsyncMode(Settings.VSync ? DisplayServer.VSyncMode.Enabled : DisplayServer.VSyncMode.Disabled);
            int bus = AudioServer.GetBusIndex("Master");
            AudioServer.SetBusVolumeDb(bus, Mathf.LinearToDb((float)System.Math.Max(Settings.MasterVolume, 0.0001)));
        }

        // ----- Scene management -----------------------------------------------------------------------

        private void SetScene(Node scene)
        {
            if (_currentScene != null)
            {
                RemoveChild(_currentScene);
                _currentScene.QueueFree();
            }
            _currentScene = scene;
            AddChild(scene);
        }

        public Node CurrentScene => _currentScene;

        public void GoToMenu()
        {
            SetScene(GD.Load<PackedScene>("res://scenes/MainMenu.tscn").Instantiate());
        }

        public void GoToEditor(VesselDesign design = null)
        {
            if (design != null) CurrentDesign = design;
            CurrentDesign ??= new VesselDesign();
            SetScene(GD.Load<PackedScene>("res://scenes/Editor.tscn").Instantiate());
        }

        /// <summary>Starts a new game in the given mode and opens the editor.</summary>
        public void NewGame(GameMode mode)
        {
            Progress = new ProgressState { Mode = mode, Funds = mode == GameMode.Career ? 40000 : 0 };
            UniversalTime = 0;
            World = null;
            CurrentDesign = null;
            CurrentSaveName = mode.ToString().ToLowerInvariant() + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
            GoToEditor(new VesselDesign());
        }

        /// <summary>Launches a design from the editor into a (possibly existing) flight world.</summary>
        public void Launch(VesselDesign design)
        {
            if (Progress.Mode == GameMode.Career)
            {
                double cost = design.TotalCost();
                if (cost > Progress.Funds)
                {
                    GD.PushWarning("Not enough funds to launch.");
                    return;
                }
                Progress.Funds -= cost;
            }
            World ??= new FlightWorld(SolarSystem, Catalog) { Time = UniversalTime };
            // Clear the launch site of landed vessels that would collide with the new one.
            foreach (Vessel v in World.Vessels.ToList())
            {
                if (v.Body == SolarSystem.HomeBody && v.Altitude < SolarSystem.HomeBody.AtmosphereHeight && v.Pinned)
                {
                    var site = CelestialBody.DirectionFromLatLon(SolarSystem.LaunchSite.Latitude, SolarSystem.LaunchSite.Longitude);
                    double dist = (v.Body.InertialToBodyFixed(v.Position, World.Time).Normalized() - site).Length * v.Body.Radius;
                    if (dist < 200) World.RemoveVessel(v);
                }
            }
            LaunchDesign = design.Clone();
            World.Launch(design.Clone());
            SetScene(GD.Load<PackedScene>("res://scenes/Flight.tscn").Instantiate());
        }

        /// <summary>Opens the flight scene for the existing world (e.g. after loading a save).</summary>
        public void ResumeFlight()
        {
            if (World == null || World.ActiveVessel == null)
            {
                GoToEditor();
                return;
            }
            SetScene(GD.Load<PackedScene>("res://scenes/Flight.tscn").Instantiate());
        }

        public void RevertToLaunch()
        {
            if (LaunchDesign == null) return;
            World = null;
            Launch(LaunchDesign);
        }

        /// <summary>Leaves the flight scene, keeping the world (vessels stay in flight).</summary>
        public void LeaveFlight()
        {
            if (World != null) UniversalTime = World.Time;
            GoToEditor();
        }

        // ----- Saves --------------------------------------------------------------------------------------

        public string SavePath(string name) => $"{SaveDir}/{name}.osp-save.json";

        public bool SaveGame(string name)
        {
            if (World == null) World = new FlightWorld(SolarSystem, Catalog) { Time = UniversalTime };
            SaveGame save = Core.Persistence.SaveGame.Capture(World, Progress, LaunchDesign, name);
            CurrentSaveName = name;
            return WriteText(SavePath(name), save.ToJson());
        }

        /// <summary>Loads a save; returns warnings, or throws <see cref="FormatException"/> if unusable.</summary>
        public List<string> LoadGame(string name)
        {
            string json = ReadText(SavePath(name));
            SaveGame save = Core.Persistence.SaveGame.FromJson(json, Catalog, SolarSystem, out List<string> warnings);
            Progress = save.Progress;
            UniversalTime = save.UniversalTime;
            LaunchDesign = save.LaunchDesign;
            World = save.Restore(SolarSystem, Catalog);
            CurrentSaveName = name;
            return warnings;
        }

        public IEnumerable<string> ListSaves()
        {
            using DirAccess dir = DirAccess.Open(SaveDir);
            if (dir == null) return Array.Empty<string>();
            return dir.GetFiles().Where(f => f.EndsWith(".osp-save.json")).Select(f => f[..^".osp-save.json".Length]).OrderBy(f => f).ToList();
        }

        public IEnumerable<string> ListVesselFiles()
        {
            using DirAccess dir = DirAccess.Open(VesselDir);
            if (dir == null) return Array.Empty<string>();
            return dir.GetFiles().Where(f => f.EndsWith(".osp-vessel.json")).OrderBy(f => f).ToList();
        }

        public bool SaveDesign(VesselDesign design, out string path)
        {
            path = VesselDir + "/" + BlueprintSerializer.FileNameFor(design.Name);
            return WriteText(path, BlueprintSerializer.Serialize(design));
        }

        public VesselDesign LoadDesign(string fileName, out List<string> warnings)
        {
            string json = ReadText(VesselDir + "/" + fileName);
            return BlueprintSerializer.Deserialize(json, Catalog, out warnings);
        }

        public bool DeleteDesign(string fileName)
        {
            return DirAccess.RemoveAbsolute(VesselDir + "/" + fileName) == Error.Ok;
        }

        public bool IsPartAvailable(PartDefinition p) => Progress.IsPartAvailable(p);

        public override void _Notification(int what)
        {
            if (what == NotificationWMCloseRequest)
            {
                SaveSettings();
            }
        }
    }
}
