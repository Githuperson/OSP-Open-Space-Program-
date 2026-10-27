using System.Collections.Generic;
using System.Text.Json;
using OSP.Core.Persistence;

namespace OSP.Game.App
{
    public enum GraphicsQuality
    {
        Low,
        Medium,
        High,
    }

    /// <summary>Player settings, stored in user://settings.json. Version 1.</summary>
    public sealed class GameSettings
    {
        public int Version { get; set; } = 1;
        public double UiScale { get; set; } = 1.0;
        public GraphicsQuality Quality { get; set; } = GraphicsQuality.Medium;
        /// <summary>3D render resolution scale (0.5 - 1.0).</summary>
        public double RenderScale { get; set; } = 1.0;
        public bool Shadows { get; set; } = true;
        public bool VSync { get; set; } = true;
        public double MouseSensitivity { get; set; } = 1.0;
        public double MasterVolume { get; set; } = 0.8;
        public bool InvertPitch { get; set; }
        /// <summary>Action name → key name (Godot keycode string), overriding the defaults.</summary>
        public Dictionary<string, string> KeyBindings { get; set; } = new();

        public string ToJson() => JsonSerializer.Serialize(this, JsonUtil.Options);

        public static GameSettings FromJson(string json)
        {
            GameSettings s = JsonSerializer.Deserialize<GameSettings>(json, JsonUtil.Options) ?? new GameSettings();
            s.UiScale = System.Math.Clamp(double.IsFinite(s.UiScale) ? s.UiScale : 1, 0.6, 2.5);
            s.RenderScale = System.Math.Clamp(double.IsFinite(s.RenderScale) ? s.RenderScale : 1, 0.5, 1.0);
            s.MouseSensitivity = System.Math.Clamp(double.IsFinite(s.MouseSensitivity) ? s.MouseSensitivity : 1, 0.1, 5);
            s.MasterVolume = System.Math.Clamp(double.IsFinite(s.MasterVolume) ? s.MasterVolume : 0.8, 0, 1);
            s.KeyBindings ??= new Dictionary<string, string>();
            return s;
        }
    }
}
