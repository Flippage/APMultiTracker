using System;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace APMultiTracker;

/// <summary>
/// Search box + Twitch category results with box art, shown in a popup that floats over the window.
/// Debounced (300 ms), cancels stale requests. Closes on pick, Escape, outside click, or window move/deactivate.
/// </summary>
public partial class CategoryPicker : UserControl
{
    private readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private CancellationTokenSource? _cts;
    private Window? _window;

    public CategoryRef? Selected { get; private set; }
    public event Action<CategoryRef?>? SelectionChanged;

    public double ResultsMaxHeight
    {
        get => Results.MaxHeight;
        set => Results.MaxHeight = value;
    }

    public CategoryPicker()
    {
        InitializeComponent();
        _debounce.Tick += async (_, _) =>
        {
            _debounce.Stop();
            await SearchAsync();
        };
        Loaded += (_, _) => HookWindow();
        Unloaded += (_, _) => { UnhookWindow(); CloseResults(); };
        IsVisibleChanged += (_, e) => { if (!(bool)e.NewValue) CloseResults(); };
    }

    // ------------------------------------------------------------ popup lifetime

    private void HookWindow()
    {
        UnhookWindow();
        _window = Window.GetWindow(this);
        if (_window == null) return;
        _window.PreviewMouseDown += Window_PreviewMouseDown;
        _window.Deactivated += Window_Changed;
        _window.LocationChanged += Window_Changed;
        _window.SizeChanged += Window_Changed;
        _window.AddHandler(ScrollViewer.ScrollChangedEvent, (ScrollChangedEventHandler)Window_Scrolled);
    }

    private void UnhookWindow()
    {
        if (_window == null) return;
        _window.PreviewMouseDown -= Window_PreviewMouseDown;
        _window.Deactivated -= Window_Changed;
        _window.LocationChanged -= Window_Changed;
        _window.SizeChanged -= Window_Changed;
        _window.RemoveHandler(ScrollViewer.ScrollChangedEvent, (ScrollChangedEventHandler)Window_Scrolled);
        _window = null;
    }

    private void Window_Changed(object? sender, EventArgs e) => CloseResults();

    // The page scrolled under the popup: close it rather than leave it floating in the wrong place.
    private void Window_Scrolled(object sender, ScrollChangedEventArgs e)
    {
        if (!ResultsPopup.IsOpen || e.VerticalChange == 0) return;
        if (e.OriginalSource is DependencyObject d && (d == Results || Results.IsAncestorOf(d))) return;
        CloseResults();
    }

    private void Window_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!ResultsPopup.IsOpen || Query.IsMouseOver) return;
        // Mouse events inside the popup also route up to the window (through the Popup's logical parent),
        // so a click on a result arrives here too. Only close for genuinely outside clicks.
        if (IsInsidePopup(e.OriginalSource as DependencyObject)) return;
        CloseResults();
    }

    private bool IsInsidePopup(DependencyObject? d)
    {
        if (ResultsPopup.Child is UIElement child && child.IsMouseOver) return true;
        while (d != null)
        {
            if (d == ResultsPopup.Child || d == Results) return true;
            d = d is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D
                ? System.Windows.Media.VisualTreeHelper.GetParent(d) ?? LogicalTreeHelper.GetParent(d)
                : LogicalTreeHelper.GetParent(d);
        }
        return false;
    }

    private void OpenResults()
    {
        if (Results.Items.Count > 0 && IsVisible) ResultsPopup.IsOpen = true;
    }

    private void CloseResults() => ResultsPopup.IsOpen = false;

    // ------------------------------------------------------------ selection

    public void SetSelected(CategoryRef? category)
    {
        Selected = category;
        SelectedCard.Visibility = category == null ? Visibility.Collapsed : Visibility.Visible;
        SelectedName.Text = category?.Name ?? "";
        Art.SetUrl(SelectedArt, category?.BoxArtUrl);
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        SetSelected(null);
        SelectionChanged?.Invoke(null);
        Query.Focus();
    }

    private void Pick(CategoryRef c)
    {
        SetSelected(c);
        CloseResults();
        Results.ItemsSource = null;
        Query.TextChanged -= Query_TextChanged;
        Query.Text = "";
        Query.TextChanged += Query_TextChanged;
        SelectionChanged?.Invoke(c);
    }

    private void Results_Click(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject src &&
            ItemsControl.ContainerFromElement(Results, src) is ListBoxItem { DataContext: CategoryRef c })
            Pick(c);
    }

    private void Results_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Results.SelectedItem is CategoryRef c)
        {
            Pick(c);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            CloseResults();
            Query.Focus();
            e.Handled = true;
        }
    }

    // ------------------------------------------------------------ search

    private void Query_TextChanged(object sender, TextChangedEventArgs e)
    {
        _debounce.Stop();
        _debounce.Start();
    }

    private void Query_GotFocus(object sender, KeyboardFocusChangedEventArgs e) => OpenResults();

    private void Query_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && ResultsPopup.IsOpen)
        {
            CloseResults();
            e.Handled = true;
        }
        else if (e.Key == Key.Down && Results.Items.Count > 0)
        {
            OpenResults();
            Results.SelectedIndex = 0;
            Results.UpdateLayout();
            (Results.ItemContainerGenerator.ContainerFromIndex(0) as ListBoxItem)?.Focus();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && Results.Items.Count > 0 && Results.Items[0] is CategoryRef first)
        {
            Pick(first);
            e.Handled = true;
        }
    }

    private async System.Threading.Tasks.Task SearchAsync()
    {
        string q = Query.Text.Trim();
        _cts?.Cancel();
        if (q.Length < 2)
        {
            Results.ItemsSource = null;
            CloseResults();
            ShowStatus(null);
            return;
        }
        if (!App.Twitch.IsSignedIn)
        {
            CloseResults();
            ShowStatus("Connect Twitch in Settings to search box art.");
            return;
        }

        var cts = _cts = new CancellationTokenSource();
        try
        {
            var list = await App.Twitch.SearchCategoriesAsync(q, cts.Token);
            if (cts.IsCancellationRequested) return;
            Results.ItemsSource = list;
            ShowStatus(list.Count == 0 ? "No categories found." : null);
            if (list.Count > 0 && Query.IsKeyboardFocusWithin) OpenResults();
            else CloseResults();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ShowStatus("Search failed: " + ex.Message); }
    }

    private void ShowStatus(string? text)
    {
        Status.Text = text ?? "";
        Status.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }
}
