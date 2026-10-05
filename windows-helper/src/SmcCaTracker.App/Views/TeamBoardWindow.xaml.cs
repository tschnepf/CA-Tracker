using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Markup;
using SmcCaTracker.App.Services;
using SmcCaTracker.App.ViewModels;
using SmcCaTracker.Core.Storage;

namespace SmcCaTracker.App.Views;

/// <summary>
/// A watchlist as a grid: its people down the side, its projects across, and in each pair it follows how many
/// open items that person holds there and how many are overdue. Clicking a count shows those items in the main
/// list; Copy as list puts every person's open items on the clipboard as text. Follows the app's counts as they
/// change (a sync, a folder change). Nothing here changes any data.
/// </summary>
public partial class TeamBoardWindow : Window
{
    private const string Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    private readonly MainViewModel viewModel;
    private TeamBoard? board;

    public TeamBoardWindow(MainViewModel viewModel, Watchlist? first)
    {
        InitializeComponent();
        ThemeManager.Attach(this);
        this.viewModel = viewModel;
        BoardGrid.AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler(Cell_Click));
        viewModel.CountsUpdated += Refresh;
        Closed += (_, _) => viewModel.CountsUpdated -= Refresh;
        FillWatchlists(first);
    }

    /// <summary>A count was clicked: show that person's items on that project (project key, person).</summary>
    public event Action<string, string>? PairChosen;

    /// <summary>Shows a watchlist (when the board is already open and another is asked for).</summary>
    public void ShowWatchlist(Watchlist list)
    {
        FillWatchlists(list);
        Activate();
    }

    private void FillWatchlists(Watchlist? select)
    {
        var lists = viewModel.Watchlists;
        WatchlistBox.ItemsSource = lists;
        WatchlistBox.SelectedItem = lists.FirstOrDefault(list => ReferenceEquals(list, select)) ?? lists.FirstOrDefault();
        Refresh();
    }

    private void Watchlist_Changed(object sender, SelectionChangedEventArgs e) => Refresh();

    private void Refresh()
    {
        // A watchlist edited meanwhile is a new object with the same name: find it again.
        if (WatchlistBox.SelectedItem is Watchlist chosen && !viewModel.Watchlists.Contains(chosen))
        {
            WatchlistBox.ItemsSource = viewModel.Watchlists;
            WatchlistBox.SelectedItem = viewModel.Watchlists.FirstOrDefault(list => string.Equals(list.Name, chosen.Name, StringComparison.OrdinalIgnoreCase))
                ?? viewModel.Watchlists.FirstOrDefault();
            return;
        }

        if (WatchlistBox.SelectedItem is not Watchlist list)
        {
            board = null;
            BoardGrid.Columns.Clear();
            BoardGrid.ItemsSource = null;
            SummaryText.Text = "";
            ShowEmpty("No watchlists yet. Make one with + New under WATCHLISTS in the project list.");
            return;
        }

        board = viewModel.TeamBoardFor(list);
        Title = $"Team board · {board.Name} — CA Tracker";
        SummaryText.Text = $"{board.People.Count} {(board.People.Count == 1 ? "person" : "people")} · {board.Projects.Count} {(board.Projects.Count == 1 ? "project" : "projects")} · "
            + $"{board.Overdue} overdue · {board.Open} open, as CA Tracker last synced them";
        BuildColumns();
        BoardGrid.ItemsSource = board.People;
        ShowEmpty(board.People.Count == 0 ? "No one is watched on this watchlist yet. Right-click it in the project list and choose Edit…." : "");
    }

    private void ShowEmpty(string text)
    {
        EmptyText.Text = text;
        EmptyText.Visibility = text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void BuildColumns()
    {
        BoardGrid.Columns.Clear();
        BoardGrid.Columns.Add(new DataGridTemplateColumn
        {
            Header = "Person",
            Width = new DataGridLength(200),
            CellTemplate = Template("""<TextBlock Text="{Binding Name}" FontWeight="SemiBold" VerticalAlignment="Center" Margin="2,0" TextTrimming="CharacterEllipsis" />""")
        });

        for (var index = 0; index < board!.Projects.Count; index++)
        {
            var project = board.Projects[index];
            var header = new StackPanel { Width = 150 };
            header.Children.Add(new TextBlock { Text = project.Name, TextWrapping = TextWrapping.Wrap });
            header.Children.Add(new TextBlock
            {
                Text = project.Overdue > 0 ? $"{project.Overdue} overdue · {project.Open} open" : $"{project.Open} open",
                FontSize = 11.5,
                FontWeight = FontWeights.Normal
            });
            BoardGrid.Columns.Add(new DataGridTemplateColumn
            {
                Header = header,
                Width = new DataGridLength(170),
                CellTemplate = Template($$"""
                    <Button Tag="{{index}}" IsEnabled="{Binding Cells[{{index}}].IsWatched, FallbackValue=False}" Cursor="Hand"
                            HorizontalAlignment="Stretch" VerticalAlignment="Stretch" Margin="0,3"
                            ToolTip="Show these items in the list">
                        <Button.Template>
                            <ControlTemplate TargetType="Button">
                                <Border x:Name="Chrome" Background="Transparent" BorderBrush="{DynamicResource LineBrush}" BorderThickness="1" CornerRadius="6" Padding="8,0">
                                    <TextBlock x:Name="Label" Text="{Binding Cells[{{index}}].Text, FallbackValue=—}" VerticalAlignment="Center"
                                               Foreground="{DynamicResource TextPrimary}" />
                                </Border>
                                <ControlTemplate.Triggers>
                                    <DataTrigger Binding="{Binding Cells[{{index}}].HasOverdue}" Value="True">
                                        <Setter TargetName="Chrome" Property="Background" Value="{DynamicResource DangerSoftBrush}" />
                                        <Setter TargetName="Chrome" Property="BorderBrush" Value="{DynamicResource DangerBrush}" />
                                        <Setter TargetName="Label" Property="Foreground" Value="{DynamicResource DangerBrush}" />
                                        <Setter TargetName="Label" Property="FontWeight" Value="SemiBold" />
                                    </DataTrigger>
                                    <Trigger Property="IsMouseOver" Value="True">
                                        <Setter TargetName="Chrome" Property="BorderBrush" Value="{DynamicResource AccentBrush}" />
                                    </Trigger>
                                    <Trigger Property="IsEnabled" Value="False">
                                        <Setter TargetName="Chrome" Property="BorderBrush" Value="Transparent" />
                                        <Setter TargetName="Label" Property="Foreground" Value="{DynamicResource TextMuted}" />
                                    </Trigger>
                                </ControlTemplate.Triggers>
                            </ControlTemplate>
                        </Button.Template>
                    </Button>
                    """)
            });
        }

        BoardGrid.Columns.Add(new DataGridTemplateColumn
        {
            Header = "In all",
            Width = new DataGridLength(160),
            CellTemplate = Template("""
                <TextBlock Text="{Binding TotalText}" VerticalAlignment="Center" Margin="2,0">
                    <TextBlock.Style>
                        <Style TargetType="TextBlock">
                            <Style.Triggers>
                                <DataTrigger Binding="{Binding HasOverdue}" Value="True">
                                    <Setter Property="Foreground" Value="{DynamicResource DangerBrush}" />
                                    <Setter Property="FontWeight" Value="SemiBold" />
                                </DataTrigger>
                            </Style.Triggers>
                        </Style>
                    </TextBlock.Style>
                </TextBlock>
                """)
        });
    }

    private static DataTemplate Template(string content) =>
        (DataTemplate)XamlReader.Parse($"<DataTemplate xmlns=\"{Presentation}\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\">{content}</DataTemplate>");

    private void Cell_Click(object sender, RoutedEventArgs e)
    {
        if (board is not null && e.OriginalSource is FrameworkElement { Tag: string tag, DataContext: BoardPerson person }
            && int.TryParse(tag, out var index) && index < board.Projects.Count)
        {
            PairChosen?.Invoke(board.Projects[index].Key, person.Name);
        }
    }

    /// <summary>Every person's open items, project by project, soonest due first, as text for an email.</summary>
    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (board is null)
        {
            return;
        }

        var text = new StringBuilder();
        text.AppendLine($"{board.Name}: open items by person, {DateTime.Today:MMMM d, yyyy}");
        foreach (var person in board.People.Where(person => person.Open > 0))
        {
            text.AppendLine();
            text.AppendLine($"{person.Name}: {person.TotalText}");
            foreach (var item in person.Cells.OfType<BoardCell>().SelectMany(cell => cell.Items))
            {
                text.AppendLine($"  {item.Project} · {item.Kind} {item.Number} · {item.Title}{(item.DueText.Length > 0 ? $" · due {item.DueText}" : "")}");
            }
        }

        try
        {
            Clipboard.SetText(text.ToString());
            CopiedText.Text = $"Copied {board.People.Sum(person => person.Open)} items";
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            CopiedText.Text = "The clipboard is busy; try again.";
        }
    }
}
