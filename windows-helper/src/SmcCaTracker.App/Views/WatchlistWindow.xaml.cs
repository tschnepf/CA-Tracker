using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Markup;
using SmcCaTracker.App.Services;
using SmcCaTracker.App.ViewModels;
using SmcCaTracker.Core.Storage;

namespace SmcCaTracker.App.Views;

/// <summary>
/// Makes or edits a watchlist: its name, the projects it covers, and, in a grid of people by project, who is
/// watched on each. Each box says what that person holds on that project now, so empty pairs are plain before
/// they're checked. People are everyone who holds or is next on an open item of a project; by default only
/// the project's own company is listed (people checked already always are). Projects not offered any more
/// (archived, removed) keep their people as they were.
/// </summary>
public partial class WatchlistWindow : Window
{
    private const string Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    private readonly List<ProjectChoice> choices;
    private readonly List<PersonRow> rows;
    private readonly ICollectionView view;
    private readonly Dictionary<string, List<string>> kept = new(StringComparer.Ordinal);

    public WatchlistWindow(IReadOnlyList<WatchProject> candidates, Watchlist? existing, IReadOnlyCollection<string> preselectedKeys)
    {
        InitializeComponent();
        ThemeManager.Attach(this);
        Title = existing is null ? "New watchlist — CA Tracker" : $"Watchlist {existing.Name} — CA Tracker";
        NameBox.Text = existing?.Name ?? "Watchlist";
        DeleteButton.Visibility = existing is null ? Visibility.Collapsed : Visibility.Visible;

        // The names already watched on a project, under its own key or (from before it had a CA folder) its Helix/Procore project's.
        List<string> Watched(WatchProject project) =>
            existing is null ? new List<string>()
            : existing.People.TryGetValue(project.Key, out var names) ? names
            : project.OtherKey is { } other && existing.People.TryGetValue(other, out var older) ? older
            : new List<string>();

        choices = candidates.Select((project, index) => new ProjectChoice(project, index)
        {
            IsChecked = existing is null
                ? preselectedKeys.Contains(project.Key)
                : existing.People.ContainsKey(project.Key) || (project.OtherKey is { } other && existing.People.ContainsKey(other))
        }).ToList();
        if (existing is not null)
        {
            var offered = candidates.SelectMany(project => new[] { project.Key, project.OtherKey }).OfType<string>().ToHashSet(StringComparer.Ordinal);
            foreach (var (key, keptNames) in existing.People.Where(entry => !offered.Contains(entry.Key)))
            {
                kept[key] = keptNames.ToList();
            }
        }

        // Everyone who holds or is next on something in any project, and everyone already watched.
        var names = candidates.SelectMany(project => project.People.Select(person => person.Name))
            .Concat(candidates.SelectMany(Watched))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        rows = names.Select(name =>
            {
                var found = candidates.Select(project => project.People.FirstOrDefault(person => string.Equals(person.Name, name, StringComparison.OrdinalIgnoreCase))).ToList();
                var cells = candidates.Select((project, index) => new Cell(found[index]?.Open ?? 0, found[index]?.Overdue ?? 0, found[index] is not null)
                {
                    IsChecked = Watched(project).Contains(name, StringComparer.OrdinalIgnoreCase)
                }).ToList();
                return new PersonRow(name, found.FirstOrDefault(person => person?.Company is not null)?.Company ?? "",
                    found.Any(person => person?.IsOurs == true), cells);
            })
            .OrderBy(row => !row.IsOurs)
            .ThenBy(row => row.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        foreach (var choice in choices)
        {
            choice.PropertyChanged += (_, _) => Rebuild();
        }

        foreach (var cell in rows.SelectMany(row => row.Cells))
        {
            cell.PropertyChanged += (_, _) => UpdateSummary();
        }

        ProjectChoices.ItemsSource = choices;
        view = CollectionViewSource.GetDefaultView(rows);
        view.Filter = item => IsListed((PersonRow)item);
        PeopleGrid.ItemsSource = view;
        PeopleGrid.AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler(GridButton_Click));
        Rebuild();
        Loaded += (_, _) =>
        {
            NameBox.Focus();
            NameBox.SelectAll();
        };
    }

    /// <summary>The watchlist to save (when the window closes with Save).</summary>
    public Watchlist? Result { get; private set; }

    /// <summary>The window closed with Delete watchlist.</summary>
    public bool Deleted { get; private set; }

    private IEnumerable<ProjectChoice> Shown => choices.Where(choice => choice.IsChecked);

    /// <summary>
    /// Who is listed: people matching the search, and of the rest those watched on a project shown, or holding
    /// or next on something in one (from its own company, unless that box is unchecked).
    /// </summary>
    private bool IsListed(PersonRow row)
    {
        var search = SearchBox.Text.Trim();
        if (search.Length > 0 && !row.Name.Contains(search, StringComparison.CurrentCultureIgnoreCase)
            && !row.Company.Contains(search, StringComparison.CurrentCultureIgnoreCase))
        {
            return false;
        }

        var shown = Shown.Select(choice => choice.Index).ToList();
        if (shown.Any(index => row.Cells[index].IsChecked))
        {
            return true;
        }

        return shown.Any(index => row.Cells[index].Listed) && (OursOnlyBox.IsChecked != true || row.IsOurs);
    }

    /// <summary>A column per project checked above, then the list and the totals again.</summary>
    private void Rebuild()
    {
        PeopleGrid.Columns.Clear();
        PeopleGrid.Columns.Add(new DataGridTemplateColumn
        {
            Header = "Person",
            Width = new DataGridLength(220),
            CellTemplate = Template("""
                <StackPanel VerticalAlignment="Center" Margin="2,0">
                    <TextBlock Text="{Binding Name}" FontWeight="SemiBold" TextTrimming="CharacterEllipsis" ToolTip="{Binding Name}" />
                    <TextBlock Text="{Binding Company}" FontSize="11.5" Foreground="{DynamicResource TextSecondary}" TextTrimming="CharacterEllipsis" />
                </StackPanel>
                """)
        });

        foreach (var choice in Shown)
        {
            var all = new Button { Content = "All", Style = (Style)FindResource("LinkButton"), Padding = new Thickness(4, 0, 4, 0), Tag = choice,
                HorizontalAlignment = HorizontalAlignment.Left, ToolTip = $"Watch everyone listed on {choice.Name}, or no one if they all are" };
            all.Click += ColumnAll_Click;
            var header = new StackPanel { Width = 128 };
            header.Children.Add(new TextBlock { Text = choice.Name, TextWrapping = TextWrapping.Wrap, ToolTip = choice.Project.CompanyName is { } company ? $"{choice.Name} · {company}" : choice.Name });
            header.Children.Add(all);
            PeopleGrid.Columns.Add(new DataGridTemplateColumn
            {
                Header = header,
                Width = new DataGridLength(140),
                CellTemplate = Template($$"""
                    <StackPanel HorizontalAlignment="Left" VerticalAlignment="Center" Margin="2,0">
                        <CheckBox IsChecked="{Binding Cells[{{choice.Index}}].IsChecked, Mode=TwoWay, UpdateSourceTrigger=PropertyChanged}"
                                  AutomationProperties.Name="{Binding Name}" />
                        <TextBlock Text="{Binding Cells[{{choice.Index}}].Label}" FontSize="11" Foreground="{DynamicResource TextSecondary}" />
                    </StackPanel>
                    """)
            });
        }

        if (Shown.Any())
        {
            PeopleGrid.Columns.Add(new DataGridTemplateColumn
            {
                Header = "Every project",
                Width = new DataGridLength(110),
                CellTemplate = Template("""
                    <Button Content="All" Tag="row" Style="{DynamicResource LinkButton}" HorizontalAlignment="Left" VerticalAlignment="Center"
                            ToolTip="Watch this person on every project above, or on none if they already are" />
                    """)
            });
        }

        view.Refresh();
        EmptyText.Text = !Shown.Any() ? "Check a project above to choose who to watch on it."
            : view.IsEmpty ? "No one to list. Clear the search, or uncheck \"Only people from the project's company\"."
            : "";
        EmptyText.Visibility = EmptyText.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateSummary();
    }

    private static DataTemplate Template(string content) =>
        (DataTemplate)XamlReader.Parse($"<DataTemplate xmlns=\"{Presentation}\">{content}</DataTemplate>");

    private void UpdateSummary()
    {
        var shown = Shown.ToList();
        var pairs = rows.SelectMany(row => shown.Where(choice => row.Cells[choice.Index].IsChecked).Select(choice => (row, cell: row.Cells[choice.Index]))).ToList();
        var people = pairs.Select(pair => pair.row).Distinct().Count();
        SummaryText.Text = shown.Count == 0 ? "No projects checked yet."
            : $"Watching {people} {(people == 1 ? "person" : "people")} on {shown.Count} {(shown.Count == 1 ? "project" : "projects")} · "
              + $"{pairs.Sum(pair => pair.cell.Open)} open · {pairs.Sum(pair => pair.cell.Overdue)} overdue";
    }

    /// <summary>A column's All: everyone listed watched on that project, or no one when they all are.</summary>
    private void ColumnAll_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: ProjectChoice choice })
        {
            return;
        }

        var listed = view.Cast<PersonRow>().ToList();
        var check = listed.Any(row => !row.Cells[choice.Index].IsChecked);
        foreach (var row in listed)
        {
            row.Cells[choice.Index].IsChecked = check;
        }
    }

    /// <summary>A person's All (in the grid): watched on every project shown, or on none when they already are.</summary>
    private void GridButton_Click(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is FrameworkElement { Tag: "row", DataContext: PersonRow row })
        {
            var shown = Shown.Select(choice => choice.Index).ToList();
            var check = shown.Any(index => !row.Cells[index].IsChecked);
            foreach (var index in shown)
            {
                row.Cells[index].IsChecked = check;
            }
        }
    }

    private void Search_Changed(object sender, TextChangedEventArgs e)
    {
        SearchHint.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (view is not null)
        {
            Rebuild();
        }
    }

    private void OursOnly_Click(object sender, RoutedEventArgs e) => Rebuild();

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var list = new Watchlist { Name = NameBox.Text };
        foreach (var (key, names) in kept)
        {
            list.People[key] = names;
        }

        foreach (var choice in Shown)
        {
            list.People[choice.Project.Key] = rows.Where(row => row.Cells[choice.Index].IsChecked).Select(row => row.Name).ToList();
        }

        if (!Shown.Any() && kept.Count == 0)
        {
            MessageBox.Show(this, "Check at least one project for the watchlist to cover.", "Watchlist", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        Result = list;
        DialogResult = true;
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        var answer = MessageBox.Show(this, $"Delete the watchlist \"{NameBox.Text.Trim()}\"? Only the watchlist goes; nothing in Helix, Procore or the CA folders changes.",
            "Delete watchlist", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (answer == MessageBoxResult.Yes)
        {
            Deleted = true;
            DialogResult = true;
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    /// <summary>A project above: whether the watchlist covers it.</summary>
    public sealed class ProjectChoice : ObservableObject
    {
        private bool isChecked;

        public ProjectChoice(WatchProject project, int index)
        {
            Project = project;
            Index = index;
        }

        public WatchProject Project { get; }
        public int Index { get; }
        public string Name => Project.Name;

        public bool IsChecked
        {
            get => isChecked;
            set => Set(ref isChecked, value);
        }
    }

    /// <summary>A person on a project: watched there or not, and what they hold there now.</summary>
    public sealed class Cell : ObservableObject
    {
        private bool isChecked;

        public Cell(int open, int overdue, bool listed)
        {
            Open = open;
            Overdue = overdue;
            Listed = listed;
        }

        public int Open { get; }
        public int Overdue { get; }

        /// <summary>They hold or are next on something on that project.</summary>
        public bool Listed { get; }

        public string Label => Open == 0 ? (Listed ? "next step only" : "nothing there")
            : Overdue > 0 ? $"{Overdue} overdue · {Open} open"
            : $"{Open} open";

        public bool IsChecked
        {
            get => isChecked;
            set => Set(ref isChecked, value);
        }
    }

    /// <summary>A person: their company, whether it's a project's own, and a cell per project.</summary>
    public sealed record PersonRow(string Name, string Company, bool IsOurs, List<Cell> Cells);
}
