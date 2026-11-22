using System;
using System.Windows;

namespace CurseTorrent.App;

public partial class MagnetWindow : Window
{
    public string Magnet { get; private set; } = "";
    public MagnetWindow()
    {
        InitializeComponent();
        WindowTheme.Attach(this);
        Loaded += (_, _) => MagnetBox.Focus();
    }
    private void OnContinue(object sender, RoutedEventArgs e)
    {
        string input = MagnetBox.Text.Trim();
        if (!input.StartsWith("magnet:?", StringComparison.OrdinalIgnoreCase))
        { ErrorLabel.Text = "Ссылка должна начинаться с magnet:?."; ErrorLabel.BringIntoView(); return; }
        Magnet = input;
        DialogResult = true;
    }
}
