using System.Windows;
using System.Windows.Media;
using DiskAnalyzer.Infrastructure.Settings;
using Microsoft.Win32;

namespace DiskAnalyzer.App.Services;

/// <summary>
/// Applies light/dark/system theme: switches the WPF Fluent theme and updates the app's own brushes
/// (App.* resource keys) so custom visuals follow the theme too.
/// </summary>
public sealed class ThemeService
{
    private ThemePreference _preference = ThemePreference.System;

    public ThemeService()
    {
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category == UserPreferenceCategory.General && _preference == ThemePreference.System)
            {
                Application.Current?.Dispatcher.BeginInvoke(() => Apply(_preference));
            }
        };
    }

    public bool IsDark { get; private set; }

    public event EventHandler? ThemeChanged;

    public void Apply(ThemePreference preference)
    {
        _preference = preference;
        bool dark = preference switch
        {
            ThemePreference.Dark => true,
            ThemePreference.Light => false,
            _ => IsSystemDark(),
        };

#pragma warning disable WPF0001
        Application.Current.ThemeMode = preference switch
        {
            ThemePreference.Dark => ThemeMode.Dark,
            ThemePreference.Light => ThemeMode.Light,
            _ => ThemeMode.System,
        };
#pragma warning restore WPF0001

        ResourceDictionary r = Application.Current.Resources;
        Set(r, "App.TextPrimary", dark ? "#FFFFFFFF" : "#E4000000");
        Set(r, "App.TextSecondary", dark ? "#C8FFFFFF" : "#9E000000");
        Set(r, "App.TextTertiary", dark ? "#8BFFFFFF" : "#72000000");
        Set(r, "App.CardBackground", dark ? "#FF2B2B2B" : "#FFFBFBFB");
        Set(r, "App.CardBackgroundAlt", dark ? "#FF323232" : "#FFF3F3F3");
        Set(r, "App.CardStroke", dark ? "#1FFFFFFF" : "#17000000");
        Set(r, "App.PageBackground", dark ? "#FF202020" : "#FFF3F3F3");
        Set(r, "App.NavBackground", dark ? "#FF1C1C1C" : "#FFEBEBEB");
        Set(r, "App.SubtleFill", dark ? "#0FFFFFFF" : "#0A000000");
        Set(r, "App.SubtleFillHover", dark ? "#18FFFFFF" : "#12000000");
        Set(r, "App.Track", dark ? "#26FFFFFF" : "#1A000000");
        Set(r, "App.Accent", dark ? "#FF4CC2FF" : "#FF0067C0");
        Set(r, "App.AccentStrong", dark ? "#FF0093E0" : "#FF005A9E");
        Set(r, "App.AccentSubtle", dark ? "#334CC2FF" : "#1F0067C0");
        Set(r, "App.OnAccent", dark ? "#FF000000" : "#FFFFFFFF");
        Set(r, "App.Warning", dark ? "#FFFCE100" : "#FF9D5D00");
        Set(r, "App.WarningBackground", dark ? "#33FCE100" : "#26FFB900");
        Set(r, "App.Danger", dark ? "#FFFF99A4" : "#FFC42B1C");
        Set(r, "App.Success", dark ? "#FF6CCB5F" : "#FF0F7B0F");
        Set(r, "App.InfoBackground", dark ? "#264CC2FF" : "#1A0067C0");
        Set(r, "App.FolderIcon", "#FFFFC83D");
        Set(r, "App.TreemapBackground", dark ? "#FF1C1C1C" : "#FFF3F3F3");

        IsDark = dark;
        ThemeChanged?.Invoke(this, EventArgs.Empty);
    }

    private static void Set(ResourceDictionary resources, string key, string color)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        brush.Freeze();
        resources[key] = brush;
    }

    private static bool IsSystemDark()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        catch (System.Security.SecurityException)
        {
            return false;
        }
    }
}
