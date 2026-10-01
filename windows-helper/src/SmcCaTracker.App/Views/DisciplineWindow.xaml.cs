using System.Windows;
using System.Windows.Controls;
using SmcCaTracker.App.Services;
using SmcCaTracker.Core.Comparison;

namespace SmcCaTracker.App.Views;

/// <summary>Chooses a discipline for one or more folders. Result null means "detect from the name".</summary>
public partial class DisciplineWindow : Window
{
    public DisciplineWindow(string subject, string? currentCode)
    {
        InitializeComponent();
        ThemeManager.Attach(this);
        SubjectText.Text = $"Discipline for {subject}";

        // The company's disciplines (or the defaults), then None and "detect".
        Options.Children.Clear();
        foreach (var discipline in Disciplines.All)
        {
            var label = string.Equals(discipline.Label, discipline.FolderToken, StringComparison.OrdinalIgnoreCase)
                ? discipline.Label
                : $"{discipline.Label} ({discipline.FolderToken})";
            Options.Children.Add(new RadioButton { GroupName = "Discipline", Content = label, Tag = discipline.Code, Margin = new Thickness(0, 0, 0, 8) });
        }

        Options.Children.Add(new RadioButton { GroupName = "Discipline", Content = "None", Tag = Disciplines.NoneCode, Margin = new Thickness(0, 0, 0, 8) });
        Options.Children.Add(new RadioButton { GroupName = "Discipline", Content = "Detect from the folder name", Tag = "", Margin = new Thickness(0, 0, 0, 8) });
        var current = currentCode ?? "";
        foreach (var option in Options.Children.OfType<RadioButton>())
        {
            option.IsChecked = string.Equals(option.Tag as string, current, StringComparison.OrdinalIgnoreCase);
        }
    }

    public string? Result { get; private set; }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var chosen = Options.Children.OfType<RadioButton>().FirstOrDefault(option => option.IsChecked == true);
        var tag = chosen?.Tag as string ?? "";
        Result = tag.Length == 0 ? null : tag;
        DialogResult = true;
    }
}
