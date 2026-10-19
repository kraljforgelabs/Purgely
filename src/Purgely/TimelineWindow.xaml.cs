using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using Purgely.Core;

namespace Purgely;

public partial class TimelineWindow : Window
{
    private readonly List<TimelineEvent> _events = new();
    private readonly ICollectionView _view;

    public TimelineWindow(bool warningsOnly = false)
    {
        InitializeComponent();
        _view = new ListCollectionView(_events);
        _view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(TimelineEvent.DateText)));
        _view.Filter = Matches;
        EventsGrid.ItemsSource = _view;
        WarningsOnly.IsChecked = warningsOnly;
        CountText.Text = "Loading…";
        // Read the history after the page is on screen – a long history must never show a white page.
        Loaded += async (_, _) =>
        {
            var events = await Task.Run(Timeline.All);
            _events.AddRange(events);
            _view.Refresh();
            // WPF DataGrid can lay out star columns before the rows exist and never correct it (columns squeezed,
            // text one letter per line). Re-assigning the widths makes it measure them again.
            foreach (var column in EventsGrid.Columns)
            {
                var width = column.Width;
                column.Width = 0;
                column.Width = width;
            }
            UpdateCount();
        };
    }

    private bool Matches(object item)
    {
        var e = (TimelineEvent)item;
        if (WarningsOnly.IsChecked == true && !e.IsWarning) return false;
        var q = SearchBox.Text.Trim();
        return q.Length == 0
               || e.Program.Contains(q, StringComparison.OrdinalIgnoreCase)
               || e.Detail.Contains(q, StringComparison.OrdinalIgnoreCase);
    }

    private void Filter_Changed(object sender, RoutedEventArgs e)
    {
        if (_view is null) return;
        _view.Refresh();
        UpdateCount();
    }

    private void UpdateCount()
    {
        var shown = _view.Cast<object>().Count();
        var warnings = _events.Count(e => e.IsWarning);
        CountText.Text = $"Showing {shown} of {_events.Count} events  •  ⚠ installed together: {warnings}";
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        var answer = Dialog.Show(this, "Clear the history?\n\n" +
                                       "\"Older than 30 days\" keeps the last month. \"Everything\" empties the list.\n\n" +
                                       "Programs and files are not touched – only this list. Purgely keeps what it needs to notice " +
                                       "removed programs and find their leftovers.",
            "Clear history", MessageBoxButton.YesNoCancel, MessageBoxImage.Question, MessageBoxResult.Yes,
            new Dictionary<MessageBoxResult, string>
            {
                [MessageBoxResult.Yes] = "Older than 30 days", [MessageBoxResult.No] = "Everything", [MessageBoxResult.Cancel] = "Cancel",
            });
        if (answer == MessageBoxResult.Cancel || answer == MessageBoxResult.None) return;
        Timeline.ClearHistory(answer == MessageBoxResult.Yes ? 30 : 0);
        _events.Clear();
        _events.AddRange(Timeline.All());
        _view.Refresh();
        UpdateCount();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
