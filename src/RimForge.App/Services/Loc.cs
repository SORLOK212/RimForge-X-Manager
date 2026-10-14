using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml.Styling;

namespace RimForge.App.Services;

/// <summary>
/// Interface language switching. Texts live in Localization/Strings.&lt;lang&gt;.axaml and are used in XAML
/// through DynamicResource, so swapping the dictionary updates the whole window at once.
/// </summary>
public static class Loc
{
    public static readonly string[] Languages = ["ru", "en"];

    public static string Current { get; private set; } = "ru";

    public static event Action? Changed;

    public static void Apply(string language)
    {
        if (!Languages.Contains(language)) language = "ru";
        var app = Application.Current!;
        var dictionaries = app.Resources.MergedDictionaries;
        var existing = dictionaries.OfType<ResourceInclude>()
            .FirstOrDefault(d => d.Source?.OriginalString.Contains("/Localization/") == true);
        if (existing is not null) dictionaries.Remove(existing);

        dictionaries.Add(new ResourceInclude(new Uri("avares://RimForge/"))
        {
            Source = new Uri($"avares://RimForge/Localization/Strings.{language}.axaml"),
        });
        Current = language;
        Changed?.Invoke();
    }

    public static string Get(string key) =>
        Application.Current!.TryGetResource(key, null, out var value) && value is string s ? s : key;

    public static string Format(string key, params object?[] args) => string.Format(Get(key), args);
}
