using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using SmcCaTracker.App.ViewModels;

namespace SmcCaTracker.App.Views;

/// <summary>
/// Sizes the item list's columns to their content. The grid only builds the rows on screen, so the
/// built-in Auto sizing would measure just those; instead each column is measured here against the
/// text of every row. Columns get exactly what their longest text needs. When that does not fit the
/// window, only the long-text columns (Title, In court, Folder, …) give up space, down to a floor, and
/// anything still too wide scrolls sideways rather than squashing every column.
/// </summary>
public partial class MainWindow
{
    // Cell padding (8 each side) plus a little room so text never touches the ellipsis.
    private const double CellPadding = 22;
    // Header padding (8 each side) plus the sort arrow.
    private const double HeaderPadding = 32;
    // A pill after a cell's text: its padding (8 each side) and the gap before it.
    private const double PillPadding = 22;
    private const double PillFontSize = 12;

    private bool fitPending;

    private sealed record ColumnFit(
        DataGridColumn Column,
        Func<RowViewModel, string> Text,
        double Floor,
        double Max,
        double Extra = 0,
        double FontSize = 0,
        bool Bold = false,
        Func<RowViewModel, string?>? Pill = null);

    private IEnumerable<ColumnFit> ColumnFits() => new[]
    {
        new ColumnFit(ProjectColumn, row => row.ProjectName, 140, 420),
        new ColumnFit(TypeColumn, row => row.TypeSingular, 0, 160),
        new ColumnFit(NumberColumn, row => row.Number, 0, 240),
        new ColumnFit(TitleColumn, row => row.TitleText, 220, 900),
        new ColumnFit(StatusColumn, row => row.ProviderStatus, 0, 220),
        new ColumnFit(CourtColumn, row => row.HolderText, 150, 600),
        // The "Done in Workload Tracker" pill follows the names on items marked complete there.
        new ColumnFit(AssignedColumn, row => row.AssignedText, 110, 420, Pill: row => row.ShowsDoneFlag ? RowViewModel.DoneFlagText : null),
        new ColumnFit(DaysTillDueColumn, row => row.DueText, 0, 260, Bold: true),
        new ColumnFit(DaysInCourtColumn, row => row.DaysInCourtText, 0, 160),
        new ColumnFit(FolderColumn, row => row.FolderName, 140, 520),
        // The "Set" link sits beside the discipline text.
        new ColumnFit(DisciplineColumn, row => row.DisciplineText, 110, 300, Extra: 44),
        // Status pill: smaller text inside 8px padding each side.
        new ColumnFit(ResultColumn, row => row.StatusText, 120, 320, Extra: 18, FontSize: 12),
    };

    private void InitColumnFitting()
    {
        viewModel.Rows.CollectionChanged += (_, _) => ScheduleColumnFit();
        Loaded += (_, _) => ScheduleColumnFit();
    }

    private void ItemsGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.WidthChanged)
        {
            ScheduleColumnFit();
        }
    }

    /// <summary>Rows arrive one Add at a time; fit once after the batch, when layout has settled.</summary>
    private void ScheduleColumnFit()
    {
        if (fitPending)
        {
            return;
        }

        fitPending = true;
        Dispatcher.InvokeAsync(() =>
        {
            fitPending = false;
            FitColumns();
        }, DispatcherPriority.Background);
    }

    private void FitColumns()
    {
        if (!IsLoaded || ItemsGrid.ActualWidth <= 0)
        {
            return;
        }

        var rows = viewModel.Rows.ToList();
        var fits = ColumnFits().Where(fit => fit.Column.Visibility == Visibility.Visible).ToList();
        var pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var typeface = new Typeface(ItemsGrid.FontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        var boldTypeface = new Typeface(ItemsGrid.FontFamily, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        var cache = new Dictionary<(string, double, bool), double>();

        double Measure(string text, double size, bool bold)
        {
            if (string.IsNullOrEmpty(text))
            {
                return 0;
            }

            if (!cache.TryGetValue((text, size, bold), out var width))
            {
                width = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                    bold ? boldTypeface : typeface, size, Brushes.Black, pixelsPerDip).WidthIncludingTrailingWhitespace;
                cache[(text, size, bold)] = width;
            }

            return width;
        }

        // What each column needs to show its longest text (and its header) in full.
        var needed = new Dictionary<DataGridColumn, double>();
        foreach (var fit in fits)
        {
            var size = fit.FontSize > 0 ? fit.FontSize : ItemsGrid.FontSize;
            var content = rows.Count == 0 ? 0 : rows.Max(row => Measure(fit.Text(row) ?? "", size, fit.Bold)
                + (fit.Pill?.Invoke(row) is { } pill ? Measure(pill, PillFontSize, false) + PillPadding : 0));
            var header = Measure(fit.Column.Header as string ?? "", ItemsGrid.FontSize, true) + HeaderPadding;
            needed[fit.Column] = Math.Ceiling(Math.Min(fit.Max, Math.Max(header, content + fit.Extra + CellPadding)));
        }

        // Room left for the measured columns once the check box, the row links and scroll bar are counted.
        var available = ItemsGrid.ActualWidth - CheckColumn.ActualWidth - ActionsColumn.Width.Value
                        - SystemParameters.VerticalScrollBarWidth - 4;

        var widths = new Dictionary<DataGridColumn, double>(needed);
        var over = widths.Values.Sum() - available;
        if (over > 0)
        {
            // Too wide: take space back from the long-text columns only, in proportion to how much each
            // has above its floor, never below the floor. What is left over scrolls sideways.
            var give = fits.Where(fit => fit.Floor > 0 && widths[fit.Column] > fit.Floor)
                .ToDictionary(fit => fit.Column, fit => widths[fit.Column] - fit.Floor);
            var totalGive = give.Values.Sum();
            if (totalGive > 0)
            {
                var share = Math.Min(1, over / totalGive);
                foreach (var (column, slack) in give)
                {
                    widths[column] = Math.Floor(widths[column] - slack * share);
                }
            }
        }

        foreach (var (column, width) in widths)
        {
            column.Width = new DataGridLength(width);
        }
    }
}
