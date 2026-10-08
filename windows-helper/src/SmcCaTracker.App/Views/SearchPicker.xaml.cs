using System.Collections;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace SmcCaTracker.App.Views;

/// <summary>
/// A drop-down list you can type into. Typing narrows the list to the entries that hold every typed word (any order,
/// ignoring case and accents); Up/Down and Enter, or a click, choose one. Leaving it with exactly one entry left
/// chooses that one; otherwise the current choice is put back.
/// </summary>
public partial class SearchPicker : UserControl
{
    public static readonly DependencyProperty ItemsSourceProperty = DependencyProperty.Register(
        nameof(ItemsSource), typeof(IEnumerable), typeof(SearchPicker), new PropertyMetadata(null, (picker, _) => ((SearchPicker)picker).Refresh()));

    public static readonly DependencyProperty SelectedItemProperty = DependencyProperty.Register(
        nameof(SelectedItem), typeof(object), typeof(SearchPicker),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (picker, _) => ((SearchPicker)picker).ShowChoice()));

    public static readonly DependencyProperty DisplayMemberPathProperty = DependencyProperty.Register(
        nameof(DisplayMemberPath), typeof(string), typeof(SearchPicker), new PropertyMetadata(null, (picker, _) => ((SearchPicker)picker).ShowChoice()));

    public static readonly DependencyProperty PlaceholderProperty = DependencyProperty.Register(
        nameof(Placeholder), typeof(string), typeof(SearchPicker), new PropertyMetadata("Type to search…"));

    /// <summary>A click that closes the list also reaches the box or the arrow; this keeps it from opening the list again.</summary>
    private const int ReopenGuardMs = 250;

    private bool showingChoice;
    private bool typed;
    private long closedAt;

    public SearchPicker()
    {
        InitializeComponent();
        ShowChoice();
    }

    public IEnumerable? ItemsSource
    {
        get => (IEnumerable?)GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    public object? SelectedItem
    {
        get => GetValue(SelectedItemProperty);
        set => SetValue(SelectedItemProperty, value);
    }

    /// <summary>The property shown for each entry, and searched.</summary>
    public string? DisplayMemberPath
    {
        get => (string?)GetValue(DisplayMemberPathProperty);
        set => SetValue(DisplayMemberPathProperty, value);
    }

    /// <summary>Shown in the empty box.</summary>
    public string Placeholder
    {
        get => (string)GetValue(PlaceholderProperty);
        set => SetValue(PlaceholderProperty, value);
    }

    public bool IsDropDownOpen => ListPopup.IsOpen;

    /// <summary>The entries shown in the list now.</summary>
    public IReadOnlyList<object> Shown => List.ItemsSource as IReadOnlyList<object> ?? [];

    /// <summary>The entries whose text holds every word of <paramref name="query"/>, in their order.</summary>
    public static IEnumerable<T> Matching<T>(IEnumerable<T> items, Func<T, string> text, string query)
    {
        var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var compare = CultureInfo.InvariantCulture.CompareInfo;
        return items.Where(item =>
        {
            var shown = text(item);
            return words.All(word => compare.IndexOf(shown, word, CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) >= 0);
        });
    }

    private string TextOf(object? item) => item is null ? ""
        : DisplayMemberPath is { Length: > 0 } path ? item.GetType().GetProperty(path)?.GetValue(item)?.ToString() ?? ""
        : item.ToString() ?? "";

    private IEnumerable<object> Items => ItemsSource?.Cast<object>() ?? [];

    private void ShowChoice()
    {
        showingChoice = true;
        Input.Text = TextOf(SelectedItem);
        showingChoice = false;
        typed = false;
        PlaceholderText.Visibility = Input.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Open()
    {
        ListPopup.IsOpen = true;
        Refresh();
    }

    /// <summary>Fills the open list: every entry with the choice marked, or the matches with the first marked.</summary>
    private void Refresh()
    {
        if (!ListPopup.IsOpen)
        {
            return;
        }

        var shown = (typed ? Matching(Items, TextOf, Input.Text) : Items).ToList();
        List.ItemsSource = shown;
        List.SelectedIndex = typed ? (shown.Count > 0 ? 0 : -1) : shown.IndexOf(SelectedItem!);
        if (List.SelectedItem is { } marked)
        {
            List.ScrollIntoView(marked);
        }

        NoMatchText.Text = $"No matches for “{Input.Text.Trim()}”";
        NoMatchText.Visibility = shown.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Choose(object item)
    {
        SetCurrentValue(SelectedItemProperty, item);
        ShowChoice();
        ListPopup.IsOpen = false;
        Input.SelectAll();
    }

    /// <summary>Typed text that leaves exactly one match chooses it; any other puts the choice back.</summary>
    private void Settle()
    {
        if (typed && Input.Text.Trim().Length > 0 && Matching(Items, TextOf, Input.Text).Take(2).ToList() is [var only])
        {
            SetCurrentValue(SelectedItemProperty, only);
        }

        ShowChoice();
    }

    private void Move(int by)
    {
        if (List.Items.Count > 0)
        {
            List.SelectedIndex = Math.Clamp(List.SelectedIndex + by, 0, List.Items.Count - 1);
            List.ScrollIntoView(List.SelectedItem);
        }
    }

    private bool JustClosed => Environment.TickCount64 - closedAt < ReopenGuardMs;

    private void Input_TextChanged(object sender, TextChangedEventArgs e)
    {
        PlaceholderText.Visibility = Input.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (!showingChoice)
        {
            typed = true;
            Open();
        }
    }

    private void Input_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var alt = Keyboard.Modifiers.HasFlag(ModifierKeys.Alt);
        switch (key)
        {
            case Key.Down or Key.Up when alt:
            case Key.F4:
                if (ListPopup.IsOpen)
                {
                    ListPopup.IsOpen = false;
                }
                else
                {
                    Open();
                }

                e.Handled = true;
                break;
            case Key.Down or Key.Up or Key.PageDown or Key.PageUp when ListPopup.IsOpen:
                Move(key switch { Key.Down => 1, Key.Up => -1, Key.PageDown => 8, _ => -8 });
                e.Handled = true;
                break;
            case Key.Down:
                Open();
                e.Handled = true;
                break;
            case Key.Enter when ListPopup.IsOpen:
                if (List.SelectedItem is { } item)
                {
                    Choose(item);
                }

                e.Handled = true;
                break;
            case Key.Escape when ListPopup.IsOpen:
                ShowChoice();
                ListPopup.IsOpen = false;
                Input.SelectAll();
                e.Handled = true;
                break;
        }
    }

    private void Input_LostFocus(object sender, RoutedEventArgs e)
    {
        Settle();
        ListPopup.IsOpen = false;
    }

    private void Input_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => Input.SelectAll();

    /// <summary>The first click selects all the text, so typing replaces it.</summary>
    private void Input_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!Input.IsKeyboardFocusWithin)
        {
            Input.Focus();
            e.Handled = true;
        }
    }

    /// <summary>
    /// A click opens the list on release: the list closes on a press outside it, so opening it on the press
    /// would close it again on the release.
    /// </summary>
    private void Input_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!ListPopup.IsOpen && !JustClosed)
        {
            Open();
        }
    }

    private void DropButton_Click(object sender, RoutedEventArgs e)
    {
        if (ListPopup.IsOpen || JustClosed)
        {
            ListPopup.IsOpen = false;
            return;
        }

        Input.Focus();
        ShowChoice();
        Input.SelectAll();
        Open();
    }

    /// <summary>An entry is chosen on release (so the release doesn't land on what is under the list), and the press is kept from the list box.</summary>
    private void List_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) =>
        e.Handled = ItemsControl.ContainerFromElement(List, e.OriginalSource as DependencyObject) is ListBoxItem;

    private void List_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (ItemsControl.ContainerFromElement(List, e.OriginalSource as DependencyObject) is ListBoxItem container)
        {
            e.Handled = true;
            Choose(List.ItemContainerGenerator.ItemFromContainer(container));
        }
    }

    /// <summary>Closed by a click elsewhere, Alt+Down or F4: settle what was typed.</summary>
    private void ListPopup_Closed(object? sender, EventArgs e)
    {
        closedAt = Environment.TickCount64;
        if (typed)
        {
            Settle();
        }
    }
}
