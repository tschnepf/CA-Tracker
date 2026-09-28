using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;
using SmcCaTracker.Core.Storage;

namespace SmcCaTracker.App.Services;

/// <summary>Switches between the Light and Dark resource dictionaries, following Windows when asked.</summary>
public static class ThemeManager
{
    private const int DwmUseImmersiveDarkMode = 20;
    private static ThemePreference preference = ThemePreference.System;
    private static bool listening;

    public static bool IsDark { get; private set; }

    public static void Apply(ThemePreference value)
    {
        preference = value;
        IsDark = value == ThemePreference.Dark || (value == ThemePreference.System && WindowsPrefersDark());
        var dictionary = new ResourceDictionary
        {
            Source = new Uri($"pack://application:,,,/SmcCaTracker;component/Themes/{(IsDark ? "Dark" : "Light")}.xaml", UriKind.Absolute)
        };
        var merged = Application.Current.Resources.MergedDictionaries;
        if (merged.Count > 0)
        {
            merged[0] = dictionary;
        }
        else
        {
            merged.Add(dictionary);
        }

        foreach (Window window in Application.Current.Windows)
        {
            ApplyTitleBar(window);
        }

        if (!listening)
        {
            listening = true;
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        }
    }

    public static void Attach(Window window)
    {
        window.SourceInitialized += (_, _) => ApplyTitleBar(window);
    }

    private static void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (preference != ThemePreference.System)
        {
            return;
        }

        Application.Current?.Dispatcher.InvokeAsync(() => Apply(ThemePreference.System));
    }

    private static bool WindowsPrefersDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static void ApplyTitleBar(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var enabled = IsDark ? 1 : 0;
        _ = DwmSetWindowAttribute(handle, DwmUseImmersiveDarkMode, ref enabled, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
