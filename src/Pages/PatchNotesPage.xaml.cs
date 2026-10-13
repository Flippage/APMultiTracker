using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace APMultiTracker;

/// <summary>
/// Every version's patch notes, newest first. The version you're running is open; older ones are collapsed under their
/// version number and open with a click, all in one scrolling list.
/// </summary>
public partial class PatchNotesPage : UserControl
{
    private static readonly Regex Inline = new(@"\*\*(.+?)\*\*|`(.+?)`", RegexOptions.Compiled);
    private readonly List<(StackPanel Body, RotateTransform Turn, TextBlock Count)> _sections = new();

    public PatchNotesPage()
    {
        InitializeComponent();
        var versions = Changelog.Versions;
        string current = App.VersionTag.TrimStart('v');
        int open = versions.ToList().FindIndex(v => v.Version.TrimStart('v') == current);
        SubText.Text = open >= 0 ? $"You're on {App.VersionTag}." : $"You're on {App.VersionTag}. Newest first.";
        if (open < 0) open = 0;
        for (int i = 0; i < versions.Count; i++)
            List.Children.Add(Section(versions[i], isCurrent: i == open && versions[i].Version.TrimStart('v') == current, expanded: i == open));
        Empty.Visibility = versions.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ToggleAllBtn.Visibility = versions.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
    }

    private Border Section(ChangelogVersion v, bool isCurrent, bool expanded)
    {
        var turn = new RotateTransform(expanded ? 0 : -90);
        var chevron = new Viewbox
        {
            Width = 11, Height = 11, Margin = new Thickness(0, 1, 12, 0), RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = turn, VerticalAlignment = VerticalAlignment.Center,
            Child = new Path
            {
                Style = (Style)FindResource("Icon"), Data = Geometry.Parse("M6,9 L12,15 L18,9"),
                Stroke = (Brush)FindResource("AccentBrush"), StrokeThickness = 3,
            },
        };
        var count = new TextBlock
        {
            Text = v.Entries.Count == 1 ? "1 change" : $"{v.Entries.Count} changes", FontSize = 12,
            Foreground = (Brush)FindResource("SubBrush"), VerticalAlignment = VerticalAlignment.Center,
            Visibility = expanded ? Visibility.Collapsed : Visibility.Visible,
        };
        DockPanel.SetDock(count, Dock.Right);

        var head = new DockPanel();
        head.Children.Add(count);
        head.Children.Add(chevron);
        head.Children.Add(new TextBlock { Text = v.Version, FontSize = 16, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center });
        if (isCurrent)
            head.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(6), Padding = new Thickness(7, 1, 7, 2), Margin = new Thickness(10, 1, 0, 0),
                Background = (Brush)FindResource("AccentDimBrush"), VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Left,
                Child = new TextBlock { Text = "CURRENT", FontSize = 9.5, FontWeight = FontWeights.Bold, Foreground = (Brush)FindResource("AccentBrush") },
            });
        else head.Children.Add(new Border());   // keeps the title from stretching

        var body = new StackPanel { Margin = new Thickness(43, 0, 24, 14), Visibility = expanded ? Visibility.Visible : Visibility.Collapsed };
        foreach (var entry in v.Entries) body.Children.Add(Entry(entry));

        var header = new Border { Background = Brushes.Transparent, Cursor = Cursors.Hand, Padding = new Thickness(20, 15, 22, 15), Child = head };
        var section = (Body: body, Turn: turn, Count: count);
        _sections.Add(section);
        header.MouseLeftButtonUp += (_, _) => { SetExpanded(section, body.Visibility != Visibility.Visible); UpdateToggleAll(); };

        var stack = new StackPanel();
        stack.Children.Add(header);
        stack.Children.Add(body);
        return new Border { Background = (Brush)FindResource("CardBrush"), CornerRadius = new CornerRadius(12), Margin = new Thickness(0, 0, 0, 10), Child = stack };
    }

    private FrameworkElement Entry(string text)
    {
        var line = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 13.5, LineHeight = 21, Foreground = (Brush)FindResource("Text2Brush") };
        int at = 0;
        foreach (Match m in Inline.Matches(text))
        {
            if (m.Index > at) line.Inlines.Add(new Run(text[at..m.Index]));
            if (m.Groups[1].Success)
                line.Inlines.Add(new Run(m.Groups[1].Value) { FontWeight = FontWeights.SemiBold, Foreground = (Brush)FindResource("TextBrush") });
            else
                line.Inlines.Add(new Run(m.Groups[2].Value) { FontFamily = (FontFamily)FindResource("MonoFont"), FontSize = 12.5 });
            at = m.Index + m.Length;
        }
        if (at < text.Length) line.Inlines.Add(new Run(text[at..]));

        var dot = new Ellipse { Width = 5, Height = 5, Fill = (Brush)FindResource("AccentBrush"), Margin = new Thickness(0, 8, 12, 0), VerticalAlignment = VerticalAlignment.Top };
        var row = new DockPanel { Margin = new Thickness(0, 4, 0, 4) };
        DockPanel.SetDock(dot, Dock.Left);
        row.Children.Add(dot);
        row.Children.Add(line);
        return row;
    }

    private static void SetExpanded((StackPanel Body, RotateTransform Turn, TextBlock Count) s, bool expanded)
    {
        s.Body.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        s.Count.Visibility = expanded ? Visibility.Collapsed : Visibility.Visible;
        s.Turn.Angle = expanded ? 0 : -90;
    }

    private bool AllExpanded => _sections.All(s => s.Body.Visibility == Visibility.Visible);

    private void UpdateToggleAll() => ToggleAllBtn.Content = AllExpanded ? "Collapse older" : "Expand all";

    private void ToggleAll_Click(object sender, RoutedEventArgs e)
    {
        bool expand = !AllExpanded;
        for (int i = 0; i < _sections.Count; i++)
            SetExpanded(_sections[i], expand || i == 0);   // collapsing keeps the newest open
        UpdateToggleAll();
    }

    private void Back_Click(object sender, RoutedEventArgs e) => ((MainWindow)Window.GetWindow(this)).ClosePatchNotes();
}
