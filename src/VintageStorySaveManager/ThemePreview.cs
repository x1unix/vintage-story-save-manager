using System.Collections.ObjectModel;
using Terminal.Gui;

namespace VintageStorySaveManager;

internal static class ThemePreview
{
    internal static void Show(Window window, ConfigurationStore configuration)
    {
        var names = ThemeManager.Instance.Keys.ToArray();
        using var dialog = new Dialog
        {
            Title = "Select theme",
            Width = 58,
            Height = 15,
            Arrangement = ViewArrangement.Fixed,
            ShadowStyle = ShadowStyle.None
        };
        var hint = new Label
        {
            Text = "Select with arrows or mouse to change theme.\nYour selection is saved automatically\nand restored when you next start the app.",
            X = 1,
            Y = 0,
            Width = Dim.Fill(1)
        };
        var list = new ListView
        {
            X = 1,
            Y = 4,
            Width = Dim.Fill(1),
            Height = Dim.Fill(2),
            BorderStyle = LineStyle.Single
        };
        list.SetSource(new ObservableCollection<string>(names));
        list.SelectedItem = Math.Max(0, Array.IndexOf(names, ThemeManager.Instance.Theme));
        var resetting = false;
        list.SelectedItemChanged += (_, _) =>
        {
            if (resetting) return;
            try
            {
                configuration.SaveTheme(names[list.SelectedItem]);
                Apply(names[list.SelectedItem], window, dialog);
            }
            catch (UserFacingException exception)
            {
                resetting = true;
                try
                {
                    list.SelectedItem = Math.Max(0, Array.IndexOf(names, ThemeManager.Instance.Theme));
                    MessageBox.ErrorQuery("Theme not saved", exception.Message, "OK");
                }
                finally { resetting = false; }
            }
        };
        var close = TuiApplication.DialogButton("_Close", isDefault: true);
        close.Accepting += (_, _) => Application.RequestStop(dialog);
        dialog.Add(hint, list);
        dialog.AddButton(close);
        dialog.Ready += (_, _) => list.SetFocus();
        Application.Run(dialog);
    }

    internal static void Apply(string name, Window window, Dialog? picker = null)
    {
        SetTheme(name);
        window.ColorScheme = Colors.ColorSchemes["Base"];
        if (picker is not null) picker.ColorScheme = Colors.ColorSchemes["Dialog"];
        window.SetNeedsDraw();
        picker?.SetNeedsDraw();
    }

    internal static void SetTheme(string name)
    {
        if (!ThemeManager.Instance.ContainsKey(name))
            throw new UserFacingException($"Unknown theme '{name}'.");

        ThemeManager.Instance.Theme = name;
        ConfigurationManager.Apply();
        // Themes also define shadows. Preserve the application's compact layouts.
        Button.DefaultShadow = ShadowStyle.None;
        Dialog.DefaultShadow = ShadowStyle.None;
        Window.DefaultShadow = ShadowStyle.None;
    }
}
