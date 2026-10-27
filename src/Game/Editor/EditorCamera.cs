using Godot;

namespace OSP.Game.Editor
{
    /// <summary>
    /// Orbit camera for the assembly building. Right-drag rotates, middle-drag (or Shift + right-drag) pans the
    /// focus vertically/horizontally, wheel zooms, Page Up/Down move the focus up and down the vessel.
    /// </summary>
    public partial class EditorCamera : Camera3D
    {
        public Vector3 Focus { get; set; } = new(0, 3, 0);
        public float Distance { get; set; } = 14;
        public float Yaw { get; set; } = 0.5f;
        public float Pitch { get; set; } = 0.25f;
        public float Sensitivity { get; set; } = 1;

        private bool _rotating, _panning;
        private float _targetDistance = 14;
        private Vector3 _targetFocus = new(0, 3, 0);

        public override void _Ready()
        {
            Fov = 55;
            Near = 0.05f;
            Far = 2000;
            Current = true;
        }

        public void Frame(Aabb bounds)
        {
            _targetFocus = bounds.GetCenter();
            _targetDistance = Mathf.Clamp(bounds.Size.Length() * 1.4f + 4, 6, 200);
        }

        public bool HandleInput(InputEvent ev)
        {
            switch (ev)
            {
                case InputEventMouseButton mb:
                    if (mb.ButtonIndex == MouseButton.Right) { _rotating = mb.Pressed && !mb.ShiftPressed; _panning = mb.Pressed && mb.ShiftPressed; return true; }
                    if (mb.ButtonIndex == MouseButton.Middle) { _panning = mb.Pressed; return true; }
                    if (mb.Pressed && mb.ButtonIndex == MouseButton.WheelUp) { _targetDistance = Mathf.Max(2, _targetDistance * 0.9f); return true; }
                    if (mb.Pressed && mb.ButtonIndex == MouseButton.WheelDown) { _targetDistance = Mathf.Min(300, _targetDistance * 1.11f); return true; }
                    break;
                case InputEventMouseMotion mm:
                    if (_rotating)
                    {
                        Yaw -= mm.Relative.X * 0.006f * Sensitivity;
                        Pitch = Mathf.Clamp(Pitch + mm.Relative.Y * 0.006f * Sensitivity, -1.4f, 1.45f);
                        return true;
                    }
                    if (_panning)
                    {
                        float k = Distance * 0.0018f;
                        _targetFocus += -GlobalBasis.X * mm.Relative.X * k + GlobalBasis.Y * mm.Relative.Y * k;
                        return true;
                    }
                    break;
                case InputEventKey { Pressed: true } key:
                    if (key.Keycode == Key.Pageup) { _targetFocus += Vector3.Up * 0.5f; return true; }
                    if (key.Keycode == Key.Pagedown) { _targetFocus += Vector3.Down * 0.5f; return true; }
                    break;
            }
            return false;
        }

        public override void _Process(double delta)
        {
            float k = 1 - Mathf.Exp(-(float)delta * 10);
            Distance += (_targetDistance - Distance) * k;
            Focus = Focus.Lerp(_targetFocus, k);
            Vector3 offset = new Vector3(Mathf.Sin(Yaw) * Mathf.Cos(Pitch), Mathf.Sin(Pitch), Mathf.Cos(Yaw) * Mathf.Cos(Pitch)) * Distance;
            Vector3 pos = Focus + offset;
            pos.Y = Mathf.Max(pos.Y, 0.3f);
            Transform = new Transform3D(Basis.LookingAt((Focus - pos).Normalized(), Vector3.Up), pos);
        }
    }
}
