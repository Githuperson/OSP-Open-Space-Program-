using System;
using System.Linq;
using Godot;
using OSP.Game.App;
using OSP.Game.UI;

namespace OSP.Game.Menu
{
    /// <summary>In-game controls reference generated from the live key bindings.</summary>
    public static class ControlsReference
    {
        public static Control Create(Action onClose)
        {
            var root = new CenterContainer { Theme = Ui.Theme };
            Ui.FullRect(root);
            root.MouseFilter = Control.MouseFilterEnum.Ignore;
            var box = Ui.VBox(4);
            box.AddChild(Ui.Label("Controls", 20, Ui.Accent));
            var grid = new GridContainer { Columns = 4 };
            grid.AddThemeConstantOverride("h_separation", 18);
            foreach (var group in InputBindings.Actions.GroupBy(a => a.Group))
            {
                grid.AddChild(Ui.Label(group.Key, 14, Ui.Accent));
                grid.AddChild(new Control());
                grid.AddChild(new Control());
                grid.AddChild(new Control());
                var items = group.ToList();
                for (int i = 0; i < items.Count; i += 2)
                {
                    for (int k = 0; k < 2; k++)
                    {
                        if (i + k < items.Count)
                        {
                            grid.AddChild(Ui.Label(items[i + k].Description, 13, Ui.TextDim));
                            grid.AddChild(Ui.Label(InputBindings.KeyName(items[i + k].Name), 13, Colors.White));
                        }
                        else
                        {
                            grid.AddChild(new Control());
                            grid.AddChild(new Control());
                        }
                    }
                }
            }
            box.AddChild(grid);
            box.AddChild(Ui.Label("Mouse: right-drag rotates the camera, wheel zooms. Editor: left-click picks/places parts, R/F rotate, X symmetry, C angle snap, Del deletes, Ctrl+Z/Y undo/redo.", 12, Ui.TextDim));
            box.AddChild(Ui.Button("Close", onClose));
            var panel = Ui.Panel(box);
            root.AddChild(panel);
            return root;
        }
    }
}
