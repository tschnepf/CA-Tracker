using System.ComponentModel;
using System.Windows.Data;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using SmcCaTracker.App.Services;
using SmcCaTracker.App.ViewModels;
using SmcCaTracker.Core.Comparison;
using SmcCaTracker.Core.Models;
using SmcCaTracker.Core.Operations;
using SmcCaTracker.Core.Storage;

namespace SmcCaTracker.App.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel viewModel;
    private readonly OperationExecutor executor;
    private readonly Dictionary<string, SignInWindow> signInWindows = new(StringComparer.OrdinalIgnoreCase);

    // Windows with their own page in the built-in browser (they share its profile with the item panel).
    private int browserWindowsOpen;
    private int downloadWindowsOpen;

    // Right-click a column heading: turn columns on or off (remembered).
    private readonly ContextMenu columnsMenu = new();

    public MainWindow(MainViewModel viewModel, AppPaths paths)
    {
        this.viewModel = viewModel;
        executor = new OperationExecutor(paths);
        InitializeComponent();

        // The discipline filter offers the company's disciplines (or the defaults).
        DisciplineBox.Items.Clear();
        DisciplineBox.Items.Add(new ComboBoxItem { Content = "All disciplines", Tag = "", IsSelected = true });
        foreach (var discipline in SmcCaTracker.Core.Comparison.Disciplines.All)
        {
            DisciplineBox.Items.Add(new ComboBoxItem { Content = discipline.Label, Tag = discipline.Code });
        }

        DisciplineBox.Items.Add(new ComboBoxItem { Content = "None", Tag = SmcCaTracker.Core.Comparison.Disciplines.NoneCode });
        InitColumnFitting();
        DataContext = viewModel;
        ThemeManager.Attach(this);
        viewModel.BrowserRefreshRequested += request =>
        {
            var window = new BrowserWindow(request, viewModel.Paths, viewModel.Settings.Browser, viewModel.Store) { Owner = this };
            browserWindowsOpen++;
            window.Closed += (_, _) => browserWindowsOpen--;
            window.Show();
        };
        // Signing out of the built-in browser clears its sign-ins: nothing may be using it.
        viewModel.BrowserProfileInUse = () =>
            browserWindowsOpen > 0 ? "the CA Tracker browser window"
            : downloadWindowsOpen > 0 ? "the download window"
            : signInWindows.Count > 0 ? "the Connect window"
            : null;
        viewModel.BrowserProfileReleasing += () =>
        {
            ItemPanel.Shutdown();
            ShowItemPanel(false);
        };
        Closed += (_, _) =>
        {
            ItemPanel.Shutdown();
            viewModel.Dispose();
        };
        ItemPanel.CloseRequested += (_, _) => ShowItemPanel(false);
        // Coming back to the app (e.g. from Explorer): check the CA folders straight away.
        Activated += (_, _) => viewModel.CheckFoldersSoon();
        GroupProjectList();
        InitColumnsMenu();
        ApplyItemGrouping();
        UpdateSortArrows();
        PreviewKeyDown += Window_PreviewKeyDown;
        viewModel.PropertyChanged += (_, args) =>
        {
            switch (args.PropertyName)
            {
                case nameof(MainViewModel.SelectedProject):
                case nameof(MainViewModel.SelectedProjects):
                    SyncProjectList();
                    UpdateColumns();
                    break;
                case nameof(MainViewModel.SortColumn):
                case nameof(MainViewModel.SortDescending):
                    UpdateSortArrows();
                    break;
                case nameof(MainViewModel.PersonFilterText):
                    UpdateColumns();
                    break;
            }
        };
        SettingsView.BackRequested += (_, _) => ShowProjects();
        SettingsView.SaveRequested += async edited =>
        {
            // Apply only what General settings edits, onto the current settings, so projects
            // changed meanwhile (for example by a sync) are kept.
            var current = viewModel.CloneSettings();
            current.Theme = edited.Theme;
            current.Folders = edited.Folders;
            current.Returned = edited.Returned;
            current.Browser = edited.Browser;
            current.Sync = edited.Sync;
            ThemeManager.Apply(current.Theme);
            await viewModel.ApplySettingsAsync(current);
        };
        // The theme is saved as soon as it's picked; only that changes.
        SettingsView.ThemeChosen += async theme =>
        {
            var current = viewModel.CloneSettings();
            current.Theme = theme;
            await viewModel.ApplySettingsAsync(current);
        };
        SettingsView.ConnectRequested += provider => OpenSignIn(provider);
        SettingsView.SyncRequested += async provider => await viewModel.SyncAsync(provider);
        Loaded += (_, _) => RestoreView();
    }

    private void Type_Checked(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel model && sender is FrameworkElement { Tag: string tag })
        {
            model.SelectedType = tag;
            UpdateColumns();
        }
    }

    /// <summary>Puts back last session's projects, filters, people, grouping and sort.</summary>
    private void RestoreView()
    {
        var remembered = viewModel.RememberedProjectIds.ToHashSet(StringComparer.Ordinal);
        var projects = viewModel.Projects.Where(project => remembered.Contains(project.Id)).ToList();
        if (projects.Count > 0)
        {
            viewModel.SetSelectedProjects(projects,
                projects.FirstOrDefault(project => project.Id == viewModel.RememberedPrimaryProjectId));
        }
        else if (viewModel.SelectedProject is null && viewModel.Projects.Count > 0)
        {
            viewModel.SelectedProject = viewModel.Projects.FirstOrDefault(project => project.HasCaFolder) ?? viewModel.Projects[0];
        }

        SyncProjectList();

        // The toolbar shows the restored choices (setting them again changes nothing in the view model).
        foreach (var radio in Descendants<RadioButton>(this))
        {
            if (radio.Tag is string tag && radio.GroupName == "Type")
            {
                radio.IsChecked = tag == viewModel.SelectedType;
            }
        }

        ShowFilterChoices();
        SelectByTag(GroupBox, viewModel.GroupBy);
        if (string.IsNullOrEmpty(viewModel.SortBy))
        {
            ShowColumnSort();
        }
        else
        {
            settingSortBox = true;
            SelectByTag(SortBox, viewModel.SortBy);
            settingSortBox = false;
        }
        ApplyItemGrouping();
        UpdateSortArrows();
        UpdateColumns();
        viewModel.ViewStateRestored();
    }

    private static void SelectByTag(ComboBox box, string tag) =>
        box.SelectedItem = box.Items.OfType<ComboBoxItem>().FirstOrDefault(item => item.Tag as string == tag);

    /// <summary>The Show, Due and Discipline boxes show the view model's filters.</summary>
    private void ShowFilterChoices()
    {
        SelectByTag(ShowBox, viewModel.Filter);
        SelectByTag(DueBox, viewModel.DueFilter.ToString());
        SelectByTag(DisciplineBox, viewModel.DisciplineFilter);
    }

    private void Show_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (DataContext is MainViewModel model && ShowBox.SelectedItem is ComboBoxItem { Tag: string tag })
        {
            model.Filter = tag;
        }
    }

    private void ClearFilters_Click(object sender, RoutedEventArgs e)
    {
        viewModel.ClearFilters();
        ShowFilterChoices();
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
        {
            if (child is T match)
            {
                yield return match;
            }

            foreach (var nested in Descendants<T>(child))
            {
                yield return nested;
            }
        }
    }

    // ---- Several projects, grouping and sorting ------------------------------------------------

    private bool syncingProjectList;

    private List<ProjectViewModel> ProcoreProjectsInSelection() =>
        ProjectList.SelectedItems.OfType<ProjectViewModel>()
            .Where(project => string.Equals(project.Project.Connection?.Provider, Providers.Procore, StringComparison.OrdinalIgnoreCase))
            .ToList();

    private List<ProjectViewModel> ProjectsInSelection() => ProjectList.SelectedItems.OfType<ProjectViewModel>().ToList();

    /// <summary>
    /// Right-clicking a project that isn't selected selects it (alone) first, so the menu acts on the
    /// project clicked rather than on the earlier selection. Right-clicking within a selection keeps it.
    /// </summary>
    private void ProjectList_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source
            && ItemsControl.ContainerFromElement(ProjectList, source) is ListBoxItem { DataContext: ProjectViewModel project }
            && !ProjectList.SelectedItems.Contains(project))
        {
            ProjectList.SelectedItems.Clear();
            ProjectList.SelectedItems.Add(project);
        }
    }

    /// <summary>
    /// Right-click: archive or unarchive the selected projects (for you only), and for Procore projects
    /// turn syncing on or off.
    /// </summary>
    private void ProjectList_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (!PrepareProjectMenu())
        {
            e.Handled = true;
            return;
        }

        ProjectList.ContextMenu!.PlacementTarget = ProjectList;
        ProjectList.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
    }

    /// <summary>Labels the project menu for the selected projects. False when none is selected.</summary>
    private bool PrepareProjectMenu()
    {
        var selected = ProjectsInSelection();
        if (selected.Count == 0)
        {
            return false;
        }

        static Visibility Show(bool value) => value ? Visibility.Visible : Visibility.Collapsed;
        var count = selected.Count;
        ArchiveProjectMenu.Header = count > 1 ? $"Archive these {count} projects (hide and stop syncing)" : "Archive (hide and stop syncing)";
        UnarchiveProjectMenu.Header = count > 1 ? $"Unarchive these {count} projects" : "Unarchive";
        FavoriteProjectMenu.Header = count > 1 ? $"Add these {count} to favorites" : "Add to favorites";
        UnfavoriteProjectMenu.Header = count > 1 ? $"Remove these {count} from favorites" : "Remove from favorites";
        FavoriteProjectMenu.Visibility = Show(selected.Any(project => !project.IsFavorite));
        UnfavoriteProjectMenu.Visibility = Show(selected.Any(project => project.IsFavorite));
        ArchiveProjectMenu.Visibility = Show(selected.Any(project => !project.IsArchived));
        UnarchiveProjectMenu.Visibility = Show(selected.Any(project => project.IsArchived));

        var procore = ProcoreProjectsInSelection().Where(project => !project.IsArchived).ToList();
        var several = procore.Count > 1;
        SyncProjectMenu.Header = several ? $"Turn syncing back on for these {procore.Count}" : "Turn syncing back on";
        SkipProjectMenu.Header = several ? $"Stop syncing these {procore.Count} (keep them listed)" : "Stop syncing (keep it listed)";
        SyncProjectMenu.Visibility = Show(procore.Any(project => project.IsSyncSkipped));
        SkipProjectMenu.Visibility = Show(procore.Any(project => !project.IsSyncSkipped));
        SyncMenuSeparator.Visibility = Show(procore.Count > 0);
        return true;
    }

    /// <summary>The ⋯ button on the project card: the same menu as right-clicking the project list.</summary>
    private void ProjectMenu_Click(object sender, RoutedEventArgs e)
    {
        var menu = ProjectList.ContextMenu;
        if (menu is null || !PrepareProjectMenu())
        {
            return;
        }

        // Below the button, right edges lined up (it sits at the card's right edge).
        menu.PlacementTarget = ProjectMenuButton;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Left;
        menu.HorizontalOffset = ProjectMenuButton.ActualWidth;
        menu.VerticalOffset = ProjectMenuButton.ActualHeight + 2;
        menu.IsOpen = true;
    }

    private void FavoriteProject_Click(object sender, RoutedEventArgs e) => viewModel.SetFavorite(ProjectsInSelection(), favorite: true);

    private void UnfavoriteProject_Click(object sender, RoutedEventArgs e) => viewModel.SetFavorite(ProjectsInSelection(), favorite: false);

    private void ArchiveProject_Click(object sender, RoutedEventArgs e) => viewModel.SetArchived(ProjectsInSelection(), archive: true);

    private void UnarchiveProject_Click(object sender, RoutedEventArgs e) => viewModel.SetArchived(ProjectsInSelection(), archive: false);

    private void ProjectsSplitter_DragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e) =>
        viewModel.ProjectsPaneWidth = ProjectsColumn.ActualWidth;

    private void SyncProject_Click(object sender, RoutedEventArgs e) => viewModel.SetSyncSkipped(ProcoreProjectsInSelection(), skipped: false);

    private void SkipProject_Click(object sender, RoutedEventArgs e) => viewModel.SetSyncSkipped(ProcoreProjectsInSelection(), skipped: true);

    private void ProjectList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (syncingProjectList)
        {
            return;
        }

        // The project clicked last is the one the header describes.
        var primary = e.AddedItems.OfType<ProjectViewModel>().LastOrDefault()
            ?? (viewModel.SelectedProject is { } current && ProjectList.SelectedItems.Contains(current) ? current : null);
        viewModel.SetSelectedProjects(ProjectList.SelectedItems.OfType<ProjectViewModel>().ToList(), primary);
    }

    /// <summary>Mirrors a selection made in code (e.g. a new project) in the project list.</summary>
    private void SyncProjectList()
    {
        var wanted = viewModel.SelectedProjects;
        var shown = ProjectList.SelectedItems.OfType<ProjectViewModel>().ToList();
        if (wanted.Count == shown.Count && wanted.All(shown.Contains))
        {
            return;
        }

        syncingProjectList = true;
        try
        {
            ProjectList.SelectedItems.Clear();
            foreach (var project in wanted)
            {
                ProjectList.SelectedItems.Add(project);
            }

            if (viewModel.SelectedProject is not null)
            {
                ProjectList.ScrollIntoView(viewModel.SelectedProject);
            }
        }
        finally
        {
            syncingProjectList = false;
        }
    }

    /// <summary>
    /// Project column when several projects are shown; Type column when both types are; the others
    /// unless turned off (right-click a heading).
    /// </summary>
    private void UpdateColumns()
    {
        ProjectColumn.Visibility = viewModel.IsMultiProject ? Visibility.Visible : Visibility.Collapsed;
        TypeColumn.Visibility = viewModel.SelectedType == ItemTypeChoices.Both ? Visibility.Visible : Visibility.Collapsed;
        foreach (var (column, name, _) in OptionalColumns())
        {
            column.Visibility = viewModel.IsColumnHidden(name) ? Visibility.Collapsed : Visibility.Visible;
        }

        // One person checked: every row is theirs, so the column would only repeat the name.
        if (viewModel.IsOnePersonFilter)
        {
            CourtColumn.Visibility = Visibility.Collapsed;
        }

        ScheduleColumnFit();
    }

    /// <summary>Columns you can turn off, with the name they are remembered by and their menu label.</summary>
    private IEnumerable<(DataGridColumn Column, string Name, string Label)> OptionalColumns() => new (DataGridColumn, string, string)[]
    {
        (DaysTillDueColumn, nameof(DaysTillDueColumn), "Due"),
        (StatusColumn, nameof(StatusColumn), "Item status"),
        (CourtColumn, nameof(CourtColumn), "Ball in court"),
        (DaysInCourtColumn, nameof(DaysInCourtColumn), "Days in court"),
        (FolderColumn, nameof(FolderColumn), "Folder"),
        (DisciplineColumn, nameof(DisciplineColumn), "Discipline"),
        (ResultColumn, nameof(ResultColumn), "Folder status")
    };

    /// <summary>
    /// View: the grouping, the sort and the columns, in one menu. The choices go through the (hidden) Group
    /// and Sort boxes, so they work as before and are remembered the same way.
    /// </summary>
    private void ViewButton_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = ViewButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };

        void Heading(string text) => menu.Items.Add(new MenuItem { Header = text, IsEnabled = false });

        void Choices(ComboBox box)
        {
            foreach (var choice in box.Items.OfType<ComboBoxItem>()
                         .Where(choice => choice.Visibility == Visibility.Visible || ReferenceEquals(choice, box.SelectedItem)))
            {
                var item = new MenuItem { Header = choice.Content, IsCheckable = true, IsChecked = ReferenceEquals(choice, box.SelectedItem) };
                item.Click += (_, _) => box.SelectedItem = choice;
                menu.Items.Add(item);
            }
        }

        Heading("Group by");
        Choices(GroupBox);
        menu.Items.Add(new Separator());
        Heading("Sort by (or click a column heading)");
        Choices(SortBox);
        menu.Items.Add(new Separator());
        Heading("Columns");
        foreach (var (_, name, label) in OptionalColumns())
        {
            var item = new MenuItem { Header = label, IsCheckable = true, StaysOpenOnClick = true, IsChecked = !viewModel.IsColumnHidden(name) };
            if (name == nameof(CourtColumn) && viewModel.IsOnePersonFilter)
            {
                item.Header = "Ball in court (hidden while one person is checked)";
            }

            item.Click += (_, _) =>
            {
                viewModel.SetColumnHidden(name, !item.IsChecked);
                UpdateColumns();
            };
            menu.Items.Add(item);
        }

        menu.IsOpen = true;
    }

    /// <summary>Right-click any column heading: a checked item per column you can turn off.</summary>
    private void InitColumnsMenu()
    {
        columnsMenu.Items.Add(new MenuItem { Header = "Show these columns:", IsEnabled = false });
        MenuItem? courtItem = null;
        foreach (var (_, name, label) in OptionalColumns())
        {
            var item = new MenuItem { Header = label, IsCheckable = true, StaysOpenOnClick = true, Tag = name };
            if (name == nameof(CourtColumn))
            {
                courtItem = item;
            }

            item.Click += (_, _) =>
            {
                viewModel.SetColumnHidden(name, !item.IsChecked);
                UpdateColumns();
            };
            columnsMenu.Items.Add(item);
        }

        columnsMenu.Opened += (_, _) =>
        {
            foreach (var item in columnsMenu.Items.OfType<MenuItem>().Where(item => item.Tag is string))
            {
                item.IsChecked = !viewModel.IsColumnHidden((string)item.Tag);
            }

            if (courtItem is not null)
            {
                courtItem.Header = viewModel.IsOnePersonFilter ? "Ball in court (hidden while one person is checked)" : "Ball in court";
            }
        };

        var headerStyle = new Style(typeof(System.Windows.Controls.Primitives.DataGridColumnHeader),
            (Style)FindResource(typeof(System.Windows.Controls.Primitives.DataGridColumnHeader)));
        headerStyle.Setters.Add(new Setter(ContextMenuProperty, columnsMenu));
        headerStyle.Setters.Add(new Setter(ToolTipProperty, "Click to sort. Right-click to choose the columns."));
        ItemsGrid.ColumnHeaderStyle = headerStyle;
    }

    private void Group_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (DataContext is MainViewModel model && GroupBox.SelectedItem is ComboBoxItem { Tag: string tag })
        {
            model.GroupBy = tag;
            ApplyItemGrouping();
        }
    }

    /// <summary>Headings in the item list. Rows arrive already ordered heading-first, then by the sort.</summary>
    private void ApplyItemGrouping()
    {
        if (CollectionViewSource.GetDefaultView(viewModel.Rows) is not ICollectionView view)
        {
            return;
        }

        view.GroupDescriptions.Clear();
        if (viewModel.GroupBy is GroupModes.Urgency)
        {
            view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(RowViewModel.UrgencyText)));
        }

        if (viewModel.GroupBy is GroupModes.Project or GroupModes.ProjectAndType)
        {
            view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(RowViewModel.ProjectName)));
        }

        if (viewModel.GroupBy is GroupModes.Type or GroupModes.ProjectAndType)
        {
            view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(RowViewModel.TypeLabel)));
        }
    }

    private bool settingSortBox;

    /// <summary>Column headings sort the whole list (within each heading when grouped).</summary>
    private void ItemsGrid_Sorting(object sender, DataGridSortingEventArgs e)
    {
        e.Handled = true;
        var column = e.Column.SortMemberPath;
        if (string.IsNullOrEmpty(column))
        {
            return;
        }

        var descending = column == viewModel.SortColumn
            ? !viewModel.SortDescending
            : column is nameof(RowViewModel.DaysInCourt);
        viewModel.SetSort(column, descending, fromHeader: true);
        ShowColumnSort();
    }

    /// <summary>Sorted by a column heading: the Sort box says which ("Title ↑") instead of going blank.</summary>
    private void ShowColumnSort()
    {
        var header = ItemsGrid.Columns.FirstOrDefault(column => column.SortMemberPath == viewModel.SortColumn)?.Header as string;
        SortByColumnItem.Content = header is null ? "By column heading" : $"{header} {(viewModel.SortDescending ? "↓" : "↑")}";
        settingSortBox = true;
        SortBox.SelectedItem = SortByColumnItem;
        settingSortBox = false;
    }

    private void UpdateSortArrows()
    {
        foreach (var column in ItemsGrid.Columns)
        {
            column.SortDirection = column.SortMemberPath == viewModel.SortColumn
                ? viewModel.SortDescending ? ListSortDirection.Descending : ListSortDirection.Ascending
                : null;
        }
    }

    private void Discipline_Checked(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel model && sender is FrameworkElement { Tag: string tag })
        {
            model.DisciplineFilter = tag;
        }
    }

    private async void SetRowDiscipline_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: RowViewModel row } && row.CanSelect)
        {
            await ChooseDisciplineAsync(new[] { row }, row.Row.Folder?.Name ?? $"{row.Number} - {row.Title}");
        }
    }

    private async void SetTickedDiscipline_Click(object sender, RoutedEventArgs e)
    {
        var rows = viewModel.CheckedRows().Where(row => row.CanSelect).ToList();
        if (rows.Count == 0)
        {
            ShowInfo("Check the items you want to set a discipline for first (the box in the heading checks everything shown).");
            return;
        }

        await ChooseDisciplineAsync(rows, $"{rows.Count} checked item{(rows.Count == 1 ? "" : "s")}");
    }

    private async Task ChooseDisciplineAsync(IReadOnlyList<RowViewModel> rows, string subject)
    {
        var current = rows.Count == 1 && rows[0].Row.DisciplineSource == DisciplineSource.Manual
            ? rows[0].Row.Discipline?.Code ?? Disciplines.NoneCode
            : null;
        var window = new DisciplineWindow(subject, current) { Owner = this };
        if (window.ShowDialog() == true)
        {
            await viewModel.SetDisciplineAsync(rows, window.Result);
        }
    }

    private DateTime personPopupClosedAt;

    private void PersonButton_Click(object sender, RoutedEventArgs e)
    {
        // Clicking the button while the list is open closes it (the click outside); don't reopen it.
        if (DateTime.UtcNow - personPopupClosedAt < TimeSpan.FromMilliseconds(250))
        {
            return;
        }

        PersonPopup.IsOpen = !PersonPopup.IsOpen;
    }

    private void PersonPopup_Closed(object? sender, EventArgs e) => personPopupClosedAt = DateTime.UtcNow;

    /// <summary>The keyboard goes into the list of people, so it can be used without a mouse.</summary>
    private void PersonPopup_Opened(object? sender, EventArgs e) =>
        Dispatcher.InvokeAsync(() => PersonPopup.Child?.MoveFocus(new TraversalRequest(FocusNavigationDirection.First)),
            System.Windows.Threading.DispatcherPriority.Input);

    private void PersonPopup_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            PersonPopup.IsOpen = false;
            PersonButton.Focus();
            e.Handled = true;
        }
    }

    private void ClearPeople_Click(object sender, RoutedEventArgs e) => viewModel.ClearPeople();

    private void ShowOnlyYou_Click(object sender, RoutedEventArgs e) => viewModel.ShowOnlyYou();

    /// <summary>A due count in the header: show just those, or all of them again when it is already on.</summary>
    private void DueChip_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { CommandParameter: string tag } && Enum.TryParse<DueFilter>(tag, out var due))
        {
            viewModel.DueFilter = viewModel.DueFilter == due ? DueFilter.Any : due;
            ShowFilterChoices();
        }
    }

    private DateTime setupPopupClosedAt;

    /// <summary>The setup chip opens the setup problems, with what can be done about them.</summary>
    private void Setup_Click(object sender, RoutedEventArgs e)
    {
        // A click on the chip while the list is open closes it (the click outside); don't reopen it.
        if (DateTime.UtcNow - setupPopupClosedAt < TimeSpan.FromMilliseconds(250))
        {
            return;
        }

        SetupPopup.IsOpen = !SetupPopup.IsOpen;
    }

    private void SetupPopup_Closed(object? sender, EventArgs e) => setupPopupClosedAt = DateTime.UtcNow;

    /// <summary>My work: your open items in every linked project. The filter boxes follow.</summary>
    private void ShowMyWork_Click(object sender, RoutedEventArgs e)
    {
        viewModel.ShowMyWork();
        ShowFilterChoices();
    }

    /// <summary>Overdue, all projects: every overdue item in every linked project.</summary>
    private void ShowAllOverdue_Click(object sender, RoutedEventArgs e)
    {
        viewModel.ShowAllOverdue();
        ShowFilterChoices();
    }

    private void Due_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (DataContext is MainViewModel model && DueBox.SelectedItem is ComboBoxItem { Tag: string tag }
            && Enum.TryParse<DueFilter>(tag, out var due))
        {
            model.DueFilter = due;
        }
    }

    private void DisciplineBox_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (DataContext is MainViewModel model && sender is ComboBox { SelectedItem: ComboBoxItem { Tag: string tag } })
        {
            model.DisciplineFilter = tag;
        }
    }

    private void Sort_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!settingSortBox && DataContext is MainViewModel model && sender is ComboBox { SelectedItem: ComboBoxItem { Tag: string tag } }
            && tag != "column")
        {
            model.SortBy = tag;
            UpdateSortArrows();
        }
    }

    /// <summary>Two sections in the project list: with a CA folder first, then without one.</summary>
    private void GroupProjectList()
    {
        if (CollectionViewSource.GetDefaultView(viewModel.Projects) is not ListCollectionView view)
        {
            return;
        }

        // Archived projects are hidden unless "Show archived" is checked.
        view.Filter = item => item is not ProjectViewModel project || viewModel.IsShownInList(project);
        view.IsLiveFiltering = true;
        view.LiveFilteringProperties.Add(nameof(ProjectViewModel.IsArchived));
        viewModel.ProjectFilterChanged += () => view.Refresh();
        ProjectsColumn.Width = new GridLength(viewModel.ProjectsPaneWidth);
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(ProjectViewModel.GroupName)));
        view.SortDescriptions.Add(new SortDescription(nameof(ProjectViewModel.GroupOrder), ListSortDirection.Ascending));
        view.SortDescriptions.Add(new SortDescription(nameof(ProjectViewModel.Name), ListSortDirection.Ascending));
        view.IsLiveGrouping = true;
        view.IsLiveSorting = true;
        view.LiveGroupingProperties.Add(nameof(ProjectViewModel.GroupName));
        view.LiveSortingProperties.Add(nameof(ProjectViewModel.GroupOrder));
        view.LiveSortingProperties.Add(nameof(ProjectViewModel.Name));
    }

    private async void NewProject_Click(object sender, RoutedEventArgs e) => await CreateProjectAsync(null);

    /// <summary>
    /// Creates a tracker project from a chosen CA folder. With <paramref name="fromProvider"/> (a Helix or
    /// Procore project listed under "No CA folder yet") the new project is already connected to it.
    /// </summary>
    private async Task CreateProjectAsync(ProjectViewModel? fromProvider)
    {
        var dialog = new OpenFolderDialog
        {
            Title = fromProvider is null ? "Choose the new project's CA folder" : $"Choose the CA folder for {fromProvider.Name}",
            Multiselect = false
        };
        // Start beside the projects already tracked (e.g. the client folder that holds the selected one).
        if (NearProjects() is { } start)
        {
            dialog.InitialDirectory = start;
        }

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        // Creating a project only records it in the tracker's settings; nothing in the folder changes.
        var draft = new TrackerProject
        {
            Name = fromProvider?.Name ?? TrackerProject.SuggestName(dialog.FolderName),
            CaFolder = dialog.FolderName,
            Connection = fromProvider is null ? null : viewModel.CloneProject(fromProvider.Project).Connection
        };
        var phase = (fromProvider is null ? null : Phases.Detect(fromProvider.Name)) ?? Phases.SuggestFromPath(dialog.FolderName);
        var existing = viewModel.Settings.Projects.FirstOrDefault(project =>
            string.Equals(Path.TrimEndingDirectorySeparator(project.CaFolder), Path.TrimEndingDirectorySeparator(dialog.FolderName), StringComparison.OrdinalIgnoreCase));
        if (existing is not null && fromProvider is not null && existing.Connection is null)
        {
            // The folder is already tracked but not connected: connect it rather than splitting it.
            var connect = MessageBox.Show(
                this,
                $"That CA folder already belongs to \"{existing.Name}\", which isn't linked to Helix or Procore.\n\nLink \"{existing.Name}\" to {fromProvider.ConnectionText}?",
                "CA folder already used",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question,
                MessageBoxResult.Yes);
            if (connect == MessageBoxResult.Yes)
            {
                var updated = viewModel.CloneProject(existing);
                updated.Connection = viewModel.CloneProject(fromProvider.Project).Connection;
                await viewModel.UpdateProjectAsync(updated);
                viewModel.SelectedProject = viewModel.Projects.FirstOrDefault(project => project.Id == updated.Id);
            }

            return;
        }

        if (existing is not null)
        {
            var answer = MessageBox.Show(
                this,
                $"That CA folder already belongs to \"{existing.Name}\".\n\nIs this the other part of the same job (Base Build / Tenant Fit Out)? Choose Yes to add it as a separate project sharing this folder.",
                "CA folder already used",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question,
                MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes)
            {
                viewModel.SelectedProject = viewModel.Projects.FirstOrDefault(project => project.Id == existing.Id);
                return;
            }

            phase = existing.Scope?.Phase switch
            {
                ProjectPhase.BaseBuild => ProjectPhase.TenantFitOut,
                ProjectPhase.TenantFitOut => ProjectPhase.BaseBuild,
                _ => ProjectPhase.TenantFitOut
            };
            draft.Scope.FilterFolders = true;
            if (fromProvider is null)
            {
                // Same Helix/Procore project for both parts: keep only this part's items.
                draft.Connection = existing.Connection is null ? null : viewModel.CloneProject(existing).Connection;
                draft.Scope.FilterItems = existing.Connection is not null;
            }
        }

        draft.Scope.Phase = phase;
        if (phase != ProjectPhase.Whole && Phases.Detect(draft.Name) != phase)
        {
            draft.Name = $"{draft.Name} {Phases.Short(phase)}";
        }
        var window = new ProjectWindow(draft, isNew: true, viewModel) { Owner = this };
        if (window.ShowDialog() == true && window.Result is not null)
        {
            viewModel.AddProject(window.Result);
            await viewModel.CompareAsync();
        }
    }

    /// <summary>
    /// The folder holding a tracked project's folder (two up from its CA folder: "…\Client\Job\CA" → "…\Client"),
    /// from the selected project or else the first tracked one; null when none exists.
    /// </summary>
    private string? NearProjects()
    {
        var folders = new[] { viewModel.SelectedProject?.Project.CaFolder }
            .Concat(viewModel.Settings.Projects.Select(project => project.CaFolder));
        foreach (var folder in folders.Where(folder => !string.IsNullOrWhiteSpace(folder)))
        {
            try
            {
                var near = Path.GetDirectoryName(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(folder!)));
                if (!string.IsNullOrEmpty(near) && Directory.Exists(near))
                {
                    return near;
                }
            }
            catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException)
            {
            }
        }

        return null;
    }

    private async void EditProject_Click(object sender, RoutedEventArgs e)
    {
        SetupPopup.IsOpen = false;
        if (viewModel.SelectedProject is null)
        {
            return;
        }

        if (viewModel.SelectedProject.IsPlaceholder)
        {
            await CreateProjectAsync(viewModel.SelectedProject);
            return;
        }

        var window = new ProjectWindow(viewModel.CloneProject(viewModel.SelectedProject.Project), isNew: false, viewModel) { Owner = this };
        if (window.ShowDialog() != true)
        {
            return;
        }

        if (window.RemoveRequested)
        {
            viewModel.RemoveProject(viewModel.SelectedProject.Id);
        }
        else if (window.Result is not null)
        {
            await viewModel.UpdateProjectAsync(window.Result);
        }
    }

    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        if (viewModel.SelectedProject is null || viewModel.SelectedProject.IsPlaceholder)
        {
            return;
        }

        var project = viewModel.CloneProject(viewModel.SelectedProject.Project);
        var window = new ConnectWindow(viewModel.KnownProviderProjects(), project.Connection) { Owner = this };
        if (window.ShowDialog() == true && window.Result is not null)
        {
            project.Connection = window.Result;
            await viewModel.UpdateProjectAsync(project);
        }
    }

    private void OpenCaFolder_Click(object sender, RoutedEventArgs e) =>
        OpenInExplorer(viewModel.SelectedProject?.Project.CaFolder);

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await viewModel.StartRefreshAsync();

    private void CancelRefresh_Click(object sender, RoutedEventArgs e) => viewModel.CancelRefresh();

    private void SelectReady_Click(object sender, RoutedEventArgs e) => viewModel.SelectAllReady();

    private void ClearTicks_Click(object sender, RoutedEventArgs e) => viewModel.ClearChecks();

    /// <summary>The heading box: checks everything shown, or unchecks it all when everything already is.</summary>
    private void TickAllShown_Click(object sender, RoutedEventArgs e) =>
        viewModel.SetAllShownChecked(viewModel.AllShownChecked != true);

    private async void MarkReturned_Click(object sender, RoutedEventArgs e)
    {
        // The checked folders that are ready to return. The other checked active folders only when none
        // is ready, and only after asking. A folder shown under both the BB and TFO projects is planned once.
        var active = viewModel.CheckedRows().Where(row => row.Row.CanMarkReturned)
            .DistinctBy(row => row.Row.Folder!.FullPath, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var rows = active.Where(row => row.Status == RowStatus.ReadyToReturn).ToList();
        if (rows.Count == 0 && active.Count > 0)
        {
            var question = active.Count == 1
                ? "The checked folder isn't ready to return (see its Folder status).\n\nMove it to Returned anyway?"
                : $"None of the {active.Count} checked folders is ready to return (see their Folder status).\n\nMove them to Returned anyway?";
            var answer = MessageBox.Show(this, $"{question} A preview shows every move first; nothing changes until you confirm it.",
                "Move to Returned", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes)
            {
                viewModel.StatusMessage = "Cancelled — nothing was changed.";
                return;
            }

            rows = active;
        }

        if (rows.Count == 0)
        {
            ShowInfo("Check the active folders you want to move into the Returned folder first. \"Check all ready to return\" checks the suggested ones.");
            return;
        }

        // Each project's folders are planned against that project's own layout, then shown together.
        await PreviewAndRunAsync(Merge(rows.GroupBy(row => row.Comparison)
            .Select(group => OperationPlanner.PlanMarkReturned(group.Select(row => row.Row), group.Key))
            .ToList()));
    }

    private async void MoveBack_Click(object sender, RoutedEventArgs e)
    {
        // A folder shown under both the BB and TFO projects is only planned once.
        var rows = viewModel.CheckedRows().Where(row => row.Row.CanMoveBack)
            .DistinctBy(row => row.Row.Folder!.FullPath, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (rows.Count == 0)
        {
            ShowInfo("Check folders that are in the Returned folder now to move them back.");
            return;
        }

        await PreviewAndRunAsync(Merge(rows.GroupBy(row => row.Comparison)
            .Select(group => OperationPlanner.PlanMoveBack(group.Select(row => row.Row), group.Key))
            .ToList()));
    }

    private async void CreateFolders_Click(object sender, RoutedEventArgs e)
    {
        SetupPopup.IsOpen = false;
        if (viewModel.Comparison is not null)
        {
            await PreviewAndRunAsync(OperationPlanner.PlanCreateTypeFolders(viewModel.Comparison));
        }
    }

    /// <summary>The folder warning's button: count folders that say neither BB nor TFO as this part.</summary>
    private async void IncludeUnlabeled_Click(object sender, RoutedEventArgs e)
    {
        SetupPopup.IsOpen = false;
        if (viewModel.SelectedProject is not { } project)
        {
            return;
        }

        var answer = MessageBox.Show(this,
            $"Also include folders and items that mention neither BB nor TFO in \"{project.Name}\"?\n\nNothing in the folders changes. You can turn this off again in Edit project → Base Build / Tenant Fit Out.",
            "Include them", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes);
        if (answer == MessageBoxResult.Yes)
        {
            await viewModel.IncludeUnlabeledAsync();
        }
    }

    private static OperationPlan Merge(IReadOnlyList<OperationPlan> plans) => plans.Count == 1
        ? plans[0]
        : new OperationPlan
        {
            Title = plans[0].Title,
            Description = plans[0].Description,
            Operations = plans.SelectMany(plan => plan.Operations).ToList()
        };

    /// <summary>Every folder change goes through here: preview, explicit confirmation, then run.</summary>
    private async Task PreviewAndRunAsync(OperationPlan plan)
    {
        if (plan.Operations.Count == 0)
        {
            ShowInfo("There is nothing to change.");
            return;
        }

        var preview = new PreviewWindow(plan, null) { Owner = this };
        if (preview.ShowDialog() != true)
        {
            viewModel.StatusMessage = "Cancelled — nothing was changed.";
            return;
        }

        viewModel.IsBusy = true;
        IReadOnlyList<OperationResult> results;
        try
        {
            results = await Task.Run(() => executor.Execute(plan));
        }
        finally
        {
            viewModel.IsBusy = false;
        }

        var done = results.Count(result => result.Outcome == OperationOutcome.Done);
        var failed = results.Count(result => result.Outcome == OperationOutcome.Failed);
        viewModel.StatusMessage = $"{plan.Title}: {done} done, {failed} failed, {results.Count - done - failed} skipped.";
        new PreviewWindow(plan, results) { Owner = this }.ShowDialog();
        // The app just changed folders: list them again rather than waiting for the background check.
        await viewModel.RecheckFoldersAsync();
    }

    private void OpenRowFolder_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: RowViewModel row })
        {
            OpenInExplorer(row.Row.Folder?.FullPath);
        }
    }

    private DownloadWindow? batchWindow;

    /// <summary>
    /// Downloads the checked items: one download window lists them all, each with its files and where they
    /// go, and saves every checked file in one go. While it is open, Download brings it back to the front.
    /// </summary>
    private void DownloadChecked_Click(object sender, RoutedEventArgs e) => OpenCheckedItems(verify: false);

    /// <summary>
    /// Checks the checked items' folders against Helix in the same window: each says whether its folder has
    /// every file Helix has for it, with the missing ones checked so Download gets them.
    /// </summary>
    private void VerifyChecked_Click(object sender, RoutedEventArgs e) => OpenCheckedItems(verify: true);

    private void OpenCheckedItems(bool verify)
    {
        if (batchWindow is not null)
        {
            batchWindow.Activate();
            return;
        }

        var rows = viewModel.CheckedRows().Where(row => row.Row.Item is not null).ToList();
        if (rows.Count == 0)
        {
            ShowInfo($"Check the items you want to {(verify ? "verify" : "download")} first (the box in the heading checks everything shown).");
            return;
        }

        var requests = rows.Select(viewModel.DownloadRequestFor).OfType<DownloadRequest>()
            .GroupBy(request => $"{request.ProjectId}|{request.ItemType}|{request.ItemId}")
            .Select(group => group.First())
            .ToList();
        var notHelix = rows.Count - requests.Count;
        if (requests.Count == 0)
        {
            ShowInfo("Downloading is available for Helix items. Procore isn't supported yet.");
            return;
        }

        if (notHelix > 0)
        {
            ShowInfo($"{notHelix} checked item{(notHelix == 1 ? " is" : "s are")} from Procore and will be skipped (Procore downloads aren't supported yet).");
        }

        batchWindow = ShowDownloadWindow(requests, verify);
        batchWindow.Closed += (_, _) => batchWindow = null;
    }

    private DownloadWindow ShowDownloadWindow(IReadOnlyList<DownloadRequest> requests, bool verify = false)
    {
        var window = new DownloadWindow(requests, viewModel.Paths, verify) { Owner = this };
        window.Downloaded += async (_, _) => await viewModel.RecheckFoldersAsync();
        downloadWindowsOpen++;
        window.Closed += (_, _) => downloadWindowsOpen--;
        window.Show();
        return window;
    }

    private void DownloadRow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: RowViewModel row })
        {
            return;
        }

        var request = viewModel.DownloadRequestFor(row);
        if (request is null)
        {
            ShowInfo("Downloading is available for Helix items. Procore isn't supported yet.");
            return;
        }

        ShowDownloadWindow(new[] { request });
    }

    /// <summary>A row's Verify: does its folder have every file Helix has for the item?</summary>
    private void VerifyRow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: RowViewModel row })
        {
            return;
        }

        var request = viewModel.DownloadRequestFor(row);
        if (request is null)
        {
            ShowInfo("Verifying folders is available for Helix items. Procore isn't supported yet.");
            return;
        }

        ShowDownloadWindow(new[] { request }, verify: true);
    }

    /// <summary>A row's Open: the newest file of a Helix item, a Procore item online, or the folder when there's no item.</summary>
    private async void OpenRow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: RowViewModel row })
        {
            return;
        }

        if (row.CanDownload)
        {
            OpenNewestFile(row);
        }
        else if (row.HasItem)
        {
            await OpenItemAsync(row);
        }
        else if (row.HasFolder)
        {
            OpenInExplorer(row.Row.Folder!.FullPath);
        }
    }

    /// <summary>A row's ▾: its menu (the same as on right-click), under the button.</summary>
    private void RowMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: RowViewModel row } button && FindResource("RowMenu") is ContextMenu menu)
        {
            menu.DataContext = row;
            menu.PlacementTarget = button;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            menu.IsOpen = true;
        }
    }

    private void OpenNewestFile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: RowViewModel row })
        {
            OpenNewestFile(row);
        }
    }

    /// <summary>
    /// Opens the newest file Helix has for a row's item: the copy in its folder when it's there, otherwise a copy
    /// from Helix kept in the app's folder (the CA folder isn't changed). An item with no files opens in Helix.
    /// </summary>
    private void OpenNewestFile(RowViewModel row)
    {
        if (viewModel.DownloadRequestFor(row) is not { } request)
        {
            return;
        }

        var window = new OpenFileWindow(request, viewModel.Paths) { Owner = this };
        window.FileOpened += message => viewModel.StatusMessage = message;
        window.NoFiles += async () =>
        {
            viewModel.StatusMessage = $"{row.TypeSingular} {row.Number} has no attachments in Helix, so it opens there instead.";
            await OpenItemAsync(row);
        };
        // It uses the built-in browser's sign-in, like the Download window.
        downloadWindowsOpen++;
        window.Closed += (_, _) => downloadWindowsOpen--;
        window.Show();
    }

    private async void OpenRowLink_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: RowViewModel row })
        {
            await OpenItemAsync(row);
        }
    }

    /// <summary>Helix items open in the item panel (Helix has no link to one item); Procore items in the web browser.</summary>
    private async Task OpenItemAsync(RowViewModel row)
    {
        if (row.Row.Item is not { } item)
        {
            return;
        }

        var connection = row.Project.Project.Connection;
        if (connection is not null && string.Equals(connection.Provider, Providers.Helix, StringComparison.OrdinalIgnoreCase))
        {
            // Helix has no link to a single item: open its list in the item panel and open the item there.
            var heading = $"{(item.ItemType == ItemTypes.Submittal ? "Submittal" : "RFI")} {item.Number} - {item.Title}";
            ShowItemPanel(true);
            await ItemPanel.OpenAsync(viewModel.Paths, viewModel.Store, item.ItemId, item.ItemType, connection.ProjectId, heading);
            return;
        }

        if (row.HasLink)
        {
            // Procore rows link straight to the item.
            Process.Start(new ProcessStartInfo(row.SourceUrl!) { UseShellExecute = true });
        }
    }

    /// <summary>Shows or hides the item panel beside the list (its width is kept while the app is open).</summary>
    private void ShowItemPanel(bool show)
    {
        if (show == (ItemPanel.Visibility == Visibility.Visible))
        {
            return;
        }

        ItemPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        PanelSplitter.Visibility = ItemPanel.Visibility;
        PanelSplitterColumn.Width = show ? new GridLength(10) : new GridLength(0);
        ItemPanelColumn.Width = show ? itemPanelWidth : new GridLength(0);
        if (!show)
        {
            itemPanelWidth = ItemPanelColumn.ActualWidth > 0 ? new GridLength(ItemPanelColumn.ActualWidth / Math.Max(1, ListColumn.ActualWidth), GridUnitType.Star) : itemPanelWidth;
        }
    }

    private GridLength itemPanelWidth = new(0.9, GridUnitType.Star);

    /// <summary>Double-click a row: open its folder, or the item when it has no folder yet.</summary>
    private async void Grid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source
            || IsInButton(source)
            || ItemsControl.ContainerFromElement(ItemsGrid, source) is not DataGridRow { Item: RowViewModel row })
        {
            // A double-click on a row's link (or check box) is that link's, not the row's.
            return;
        }

        await OpenRowAsync(row);
    }

    /// <summary>
    /// Enter opens the selected row, as a double-click does; Space checks (or unchecks) the selected rows.
    /// On a row's link or check box the keys are that control's own.
    /// </summary>
    private async void ItemsGrid_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.None
            || e.OriginalSource is TextBox or System.Windows.Controls.Primitives.ButtonBase
            || ItemsGrid.SelectedItems.OfType<RowViewModel>().ToList() is not { Count: > 0 } rows)
        {
            return;
        }

        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await OpenRowAsync(ItemsGrid.CurrentItem as RowViewModel ?? rows[0]);
        }
        else if (e.Key == Key.Space)
        {
            e.Handled = true;
            var check = !rows.Where(row => row.CanSelect).All(row => row.IsChecked);
            foreach (var row in rows.Where(row => row.CanSelect))
            {
                row.IsChecked = check;
            }
        }
    }

    /// <summary>Opens a row's folder, or its item when it has no folder yet.</summary>
    private async Task OpenRowAsync(RowViewModel row)
    {
        if (row.HasFolder)
        {
            OpenInExplorer(row.Row.Folder!.FullPath);
        }
        else if (row.HasItem)
        {
            await OpenItemAsync(row);
        }
    }

    private static bool IsInButton(DependencyObject source)
    {
        for (DependencyObject? element = source; element is not null and not DataGridRow;
             element = element is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D
                 ? System.Windows.Media.VisualTreeHelper.GetParent(element)
                 : LogicalTreeHelper.GetParent(element))
        {
            if (element is System.Windows.Controls.Primitives.ButtonBase)
            {
                return true;
            }
        }

        return false;
    }

    private void OpenSignIn(string provider, bool signInExpired = false)
    {
        if (signInWindows.TryGetValue(provider, out var open))
        {
            open.Activate();
            return;
        }

        var window = new SignInWindow(provider, viewModel.Paths, viewModel.Store) { Owner = this, SignInExpired = signInExpired };
        signInWindows[provider] = window;
        var started = false;
        var isProcore = ProviderSites.IsProcore(provider);
        if (isProcore)
        {
            // The Connect window lists your Procore projects on its own signed-in page; then every
            // project is synced. If it can't, the app looks for them in the background instead.
            window.ProcoreProjectsFound += discovery =>
            {
                started = true;
                viewModel.OnProcoreProjectsFound(discovery);
            };
            window.ProcoreDiscoveryFailed += _ =>
            {
                started = true;
                viewModel.OnConnected(provider, signedIn: true);
            };
        }
        else
        {
            window.SignedInDetected += () => viewModel.OnConnected(provider, signedIn: true);
        }

        window.Closed += (_, _) =>
        {
            signInWindows.Remove(provider);
            // Connect to Helix may have listed new projects under "No CA folder yet", and a fresh
            // sign-in lets background reading try again. A Procore window closed before it finished
            // listing projects hands the search to the background.
            viewModel.OnConnected(provider, signedIn: isProcore && window.SignedIn && !started);
        };
        window.Show();
    }

    /// <summary>Settings, or back to the projects when Settings is open (asking about unsaved changes).</summary>
    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        if (SettingsView.Visibility != Visibility.Visible)
        {
            ShowSettings(SettingsSection.General);
        }
        else if (SettingsView.ConfirmLeave())
        {
            ShowProjects();
        }
    }

    private async void InstallUpdate_Click(object sender, RoutedEventArgs e)
    {
        var answer = MessageBox.Show(this,
            $"{viewModel.UpdateText.TrimEnd('…')} now? CA Tracker closes, installs it and starts again.\n\n{viewModel.UpdateNotes}\n\nFrom: {viewModel.UpdateSource}",
            "Install update", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes);
        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        var problem = await viewModel.StartUpdateAsync();
        if (problem is null)
        {
            Application.Current.Shutdown();
            return;
        }

        ShowInfo($"The update couldn't start: {problem}");
    }

    /// <summary>Shows the Settings page in place of the project list.</summary>
    private void ShowSettings(SettingsSection section)
    {
        viewModel.RefreshAccounts();
        if (SettingsView.Visibility != Visibility.Visible)
        {
            SettingsView.Open(viewModel.CloneSettings, viewModel.Paths, section);
        }
        else
        {
            SettingsView.ShowSectionFromOutside(section);
        }

        ProjectsPage.Visibility = Visibility.Collapsed;
        SettingsView.Visibility = Visibility.Visible;
    }

    private void ShowProjects()
    {
        SettingsView.Visibility = Visibility.Collapsed;
        ProjectsPage.Visibility = Visibility.Visible;
    }

    /// <summary>Sync all (or Stop while it runs). With nothing signed in, Connections opens instead.</summary>
    private async void SyncAll_Click(object sender, RoutedEventArgs e)
    {
        if (viewModel.IsAnySyncing)
        {
            viewModel.StopSyncs();
            return;
        }

        if (!await viewModel.SyncAllAsync())
        {
            ShowSettings(SettingsSection.Connections);
        }
    }

    private void SyncMenu_Click(object sender, RoutedEventArgs e)
    {
        var menu = SyncMenuButton.ContextMenu!;
        SyncHelixMenu.IsEnabled = viewModel.ConnectionFor(Providers.Helix).CanSync;
        SyncProcoreMenu.IsEnabled = viewModel.ConnectionFor(Providers.Procore).CanSync;
        menu.PlacementTarget = SyncMenuButton;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private async void SyncHelix_Click(object sender, RoutedEventArgs e) => await viewModel.SyncAsync(Providers.Helix);

    private async void SyncProcore_Click(object sender, RoutedEventArgs e) => await viewModel.SyncAsync(Providers.Procore);

    private void OpenConnections_Click(object sender, RoutedEventArgs e) => ShowSettings(SettingsSection.Connections);

    private void DismissBanner_Click(object sender, RoutedEventArgs e) => viewModel.DismissSignInBanner();

    private void BannerSignIn_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ConnectionViewModel connection })
        {
            OpenSignIn(connection.Provider, signInExpired: connection.NeedsSignIn);
        }
    }

    /// <summary>F5 syncs the selected project, Ctrl+F finds a project, Esc closes the item panel.</summary>
    private async void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (ProjectsPage.Visibility != Visibility.Visible)
        {
            return;
        }

        if (e.Key == Key.F5 && Keyboard.Modifiers == ModifierKeys.None)
        {
            e.Handled = true;
            if (viewModel.CanRefresh)
            {
                await viewModel.StartRefreshAsync();
            }
        }
        else if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            ProjectSearchBox.Focus();
            ProjectSearchBox.SelectAll();
        }
        else if (e.Key == Key.Escape && ItemPanel.Visibility == Visibility.Visible && !PersonPopup.IsOpen
                 && Keyboard.FocusedElement is not TextBox)
        {
            e.Handled = true;
            ShowItemPanel(false);
        }
    }

    /// <summary>Esc in "Find a project" clears it; Enter goes to the list.</summary>
    private void ProjectSearch_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && ProjectSearchBox.Text.Length > 0)
        {
            ProjectSearchBox.Clear();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && ProjectList.Items.Count > 0)
        {
            ProjectList.SelectedIndex = 0;
            (ProjectList.ItemContainerGenerator.ContainerFromIndex(0) as UIElement)?.Focus();
            e.Handled = true;
        }
    }

    private void OpenInExplorer(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            ShowInfo("That folder can't be found. Select Sync (or Rescan CA folder) to update the list.");
            return;
        }

        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
    }

    private void ShowInfo(string message) =>
        MessageBox.Show(this, message, "CA Tracker", MessageBoxButton.OK, MessageBoxImage.Information);
}
