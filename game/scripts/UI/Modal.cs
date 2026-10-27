using System;
using Godot;

namespace OSP.Game.UI;

/// <summary>Centered dialog over a dimmed backdrop. Add to a CanvasLayer / Control; call Close() to remove it.</summary>
public sealed partial class Modal : Control
{
    public Action? Closed;

    public static Modal Show(Node parent, string title, Control content, Vector2? size = null, bool closeButton = true)
    {
        var m = new Modal();
        m.SetAnchorsPreset(LayoutPreset.FullRect);
        m.MouseFilter = MouseFilterEnum.Stop;
        var dim = new ColorRect { Color = new Color(0, 0, 0, 0.55f), MouseFilter = MouseFilterEnum.Stop };
        dim.SetAnchorsPreset(LayoutPreset.FullRect);
        m.AddChild(dim);

        var center = new CenterContainer(); center.SetAnchorsPreset(LayoutPreset.FullRect);
        m.AddChild(center);
        var panel = new PanelContainer { CustomMinimumSize = size ?? new Vector2(560, 0) };
        center.AddChild(panel);
        var margin = new MarginContainer();
        foreach (var s in new[] { "left", "right", "top", "bottom" }) margin.AddThemeConstantOverride("margin_" + s, 14);
        panel.AddChild(margin);
        var v = Ui.VBox(10);
        margin.AddChild(v);
        var head = Ui.HBox();
        head.AddChild(Ui.Label(title, 22, Ui.Accent));
        head.AddChild(Ui.Filler());
        if (closeButton) head.AddChild(Ui.Button("✕", m.Close));
        v.AddChild(head);
        v.AddChild(Ui.Sep());
        v.AddChild(content);
        parent.AddChild(m);
        return m;
    }

    public void Close()
    {
        Closed?.Invoke();
        QueueFree();
    }
}
