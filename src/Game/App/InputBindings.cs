using System.Collections.Generic;
using System.Linq;
using Godot;

namespace OSP.Game.App
{
    /// <summary>
    /// Input actions and their default keys. Actions are registered in the InputMap at start-up so bindings can be
    /// changed in the settings menu (stored in <see cref="GameSettings.KeyBindings"/>).
    /// </summary>
    public static class InputBindings
    {
        public sealed class ActionInfo
        {
            public string Name;
            public string Description;
            public Key DefaultKey;
            public string Group;
        }

        public static readonly List<ActionInfo> Actions = new()
        {
            new() { Name = "pitch_down", Description = "Pitch down", DefaultKey = Key.W, Group = "Flight" },
            new() { Name = "pitch_up", Description = "Pitch up", DefaultKey = Key.S, Group = "Flight" },
            new() { Name = "yaw_left", Description = "Yaw left", DefaultKey = Key.A, Group = "Flight" },
            new() { Name = "yaw_right", Description = "Yaw right", DefaultKey = Key.D, Group = "Flight" },
            new() { Name = "roll_left", Description = "Roll left", DefaultKey = Key.Q, Group = "Flight" },
            new() { Name = "roll_right", Description = "Roll right", DefaultKey = Key.E, Group = "Flight" },
            new() { Name = "throttle_up", Description = "Throttle up", DefaultKey = Key.Shift, Group = "Flight" },
            new() { Name = "throttle_down", Description = "Throttle down", DefaultKey = Key.Ctrl, Group = "Flight" },
            new() { Name = "throttle_full", Description = "Full throttle", DefaultKey = Key.Z, Group = "Flight" },
            new() { Name = "throttle_cut", Description = "Cut throttle", DefaultKey = Key.X, Group = "Flight" },
            new() { Name = "stage", Description = "Activate next stage", DefaultKey = Key.Space, Group = "Flight" },
            new() { Name = "toggle_sas", Description = "Toggle SAS", DefaultKey = Key.T, Group = "Flight" },
            new() { Name = "toggle_rcs", Description = "Toggle RCS", DefaultKey = Key.R, Group = "Flight" },
            new() { Name = "precision", Description = "Toggle precision control", DefaultKey = Key.Capslock, Group = "Flight" },
            new() { Name = "translate_forward", Description = "RCS forward", DefaultKey = Key.H, Group = "RCS" },
            new() { Name = "translate_back", Description = "RCS back", DefaultKey = Key.N, Group = "RCS" },
            new() { Name = "translate_left", Description = "RCS left", DefaultKey = Key.J, Group = "RCS" },
            new() { Name = "translate_right", Description = "RCS right", DefaultKey = Key.L, Group = "RCS" },
            new() { Name = "translate_up", Description = "RCS up", DefaultKey = Key.I, Group = "RCS" },
            new() { Name = "translate_down", Description = "RCS down", DefaultKey = Key.K, Group = "RCS" },
            new() { Name = "toggle_gear", Description = "Deploy/retract landing legs", DefaultKey = Key.G, Group = "Systems" },
            new() { Name = "toggle_panels", Description = "Deploy/retract solar panels", DefaultKey = Key.U, Group = "Systems" },
            new() { Name = "map_view", Description = "Toggle map view", DefaultKey = Key.M, Group = "View" },
            new() { Name = "camera_mode", Description = "Cycle camera mode", DefaultKey = Key.V, Group = "View" },
            new() { Name = "camera_reset", Description = "Reset camera", DefaultKey = Key.Backspace, Group = "View" },
            new() { Name = "warp_up", Description = "Increase time warp", DefaultKey = Key.Period, Group = "Time" },
            new() { Name = "warp_down", Description = "Decrease time warp", DefaultKey = Key.Comma, Group = "Time" },
            new() { Name = "warp_stop", Description = "Stop time warp", DefaultKey = Key.Slash, Group = "Time" },
            new() { Name = "pause", Description = "Pause menu", DefaultKey = Key.Escape, Group = "Game" },
            new() { Name = "quicksave", Description = "Quicksave", DefaultKey = Key.F5, Group = "Game" },
            new() { Name = "quickload", Description = "Quickload", DefaultKey = Key.F9, Group = "Game" },
            new() { Name = "vessel_next", Description = "Switch to next vessel", DefaultKey = Key.Bracketright, Group = "Game" },
            new() { Name = "vessel_prev", Description = "Switch to previous vessel", DefaultKey = Key.Bracketleft, Group = "Game" },
            new() { Name = "toggle_help", Description = "Show controls", DefaultKey = Key.F1, Group = "Game" },
            new() { Name = "toggle_ui", Description = "Hide/show interface", DefaultKey = Key.F2, Group = "Game" },
        };

        /// <summary>Registers every action with its current binding.</summary>
        public static void Apply(GameSettings settings)
        {
            foreach (ActionInfo a in Actions)
            {
                if (InputMap.HasAction(a.Name)) InputMap.EraseAction(a.Name);
                InputMap.AddAction(a.Name, 0.2f);
                Key key = a.DefaultKey;
                if (settings.KeyBindings.TryGetValue(a.Name, out string name))
                {
                    Key parsed = OS.FindKeycodeFromString(name);
                    if (parsed != Key.None) key = parsed;
                }
                InputMap.ActionAddEvent(a.Name, new InputEventKey { PhysicalKeycode = key });
            }
        }

        public static Key CurrentKey(string action)
        {
            if (!InputMap.HasAction(action)) return Key.None;
            InputEventKey ev = InputMap.ActionGetEvents(action).OfType<InputEventKey>().FirstOrDefault();
            return ev?.PhysicalKeycode ?? Key.None;
        }

        public static string KeyName(string action)
        {
            Key k = CurrentKey(action);
            return k == Key.None ? "—" : OS.GetKeycodeString(k);
        }
    }
}
