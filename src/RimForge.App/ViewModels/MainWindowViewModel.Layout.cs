using RimForge.Core.Storage;

namespace RimForge.App.ViewModels;

/// <summary>What the player made of the window: panel widths and the window's own place, kept between starts.</summary>
public sealed partial class MainWindowViewModel
{
    public double? LayoutValue(string key) => _settings.Layout.TryGetValue(key, out var value) && double.IsFinite(value) ? value : null;

    /// <summary>Remembers a panel's width; null goes back to the usual one.</summary>
    public void SetLayout(string key, double? value)
    {
        if (value is { } v && double.IsFinite(v)) _settings.Layout[key] = Math.Round(v, 3);
        else _settings.Layout.Remove(key);
        _settings.Save();
    }

    public WindowPlace? WindowPlace
    {
        get => _settings.Window;
        set
        {
            _settings.Window = value;
            _settings.Save();
        }
    }
}
