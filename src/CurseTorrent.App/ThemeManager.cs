using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Windows;
using System.Windows.Media;

namespace CurseTorrent.App;

internal static class ThemeManager
{
    private static Dictionary<string, Color>? _darkPalette;
    private static bool _initialized;

    internal static void Initialize()
    {
        if (_initialized) return;
        _initialized = true;
        var application = System.Windows.Application.Current;
        ApplyPalette(application.Resources, SystemParameters.HighContrast);
        SystemParameters.StaticPropertyChanged += OnSystemParametersChanged;
        application.Exit += (_, _) => SystemParameters.StaticPropertyChanged -= OnSystemParametersChanged;
    }

    private static void OnSystemParametersChanged(object? sender, PropertyChangedEventArgs args)
    {
        var application = System.Windows.Application.Current;
        if (application is null || application.Dispatcher.HasShutdownStarted) return;
        if (args.PropertyName != nameof(SystemParameters.HighContrast) && !SystemParameters.HighContrast) return;
        application.Dispatcher.BeginInvoke(new Action(() =>
        {
            ApplyPalette(application.Resources, SystemParameters.HighContrast);
            foreach (Window window in application.Windows) WindowTheme.ApplyNativeTheme(window);
        }));
    }

    internal static void ApplyPalette(ResourceDictionary resources, bool highContrast)
    {
        if (_darkPalette is null)
        {
            _darkPalette = new Dictionary<string, Color>(StringComparer.Ordinal);
            foreach (DictionaryEntry resource in resources)
                if (resource.Key is string key && resource.Value is SolidColorBrush brush)
                    _darkPalette[key] = brush.Color;
        }
        foreach (var entry in _darkPalette)
        {
            var brush = new SolidColorBrush(highContrast ? AccessibleColor(entry.Key) : entry.Value);
            brush.Freeze();
            resources[entry.Key] = brush;
        }
    }

    private static Color AccessibleColor(string key) => key switch
    {
        "TextBrush" or "MutedBrush" or "ErrorBrush" or "SuccessBrush" or "IllustrationBrush" or
        "BorderBrush" or "ControlBorderBrush" or "PrimaryBorderBrush" or "FocusBrush" or "ScrollThumbBrush" => SystemColors.WindowTextColor,
        "ButtonTextBrush" => SystemColors.ControlTextColor,
        "SelectionTextBrush" or "CheckedTextBrush" => SystemColors.HighlightTextColor,
        "AccentBrush" or "SelectionBrush" or "InputSelectionBrush" or "PressedBrush" => SystemColors.HighlightColor,
        "RaisedBrush" or "PrimaryBrush" or "HoverBrush" => SystemColors.ControlColor,
        _ => SystemColors.WindowColor
    };
}
