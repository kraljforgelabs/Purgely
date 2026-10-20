using System.Windows;
using Purgely.Core;

namespace Purgely;

public partial class RelatedWindow : Window
{
    private readonly List<RelatedProgram> _related;

    /// <summary>The programs the user chose to remove (empty if none).</summary>
    public List<InstalledProgram> Chosen { get; } = new();

    public RelatedWindow(string mainName, List<RelatedProgram> related)
    {
        InitializeComponent();
        _related = related;
        TitleText.Text = $"Remove the rest of \"{mainName}\" too?";
        RelatedGrid.ItemsSource = related;
    }

    private void Uninstall_Click(object sender, RoutedEventArgs e)
    {
        var chosen = _related.Where(r => r.IsChecked).Select(r => r.Program).ToList();
        if (chosen.Count == 0)
        {
            Dialog.Show(this, "Nothing is ticked.", "Related programs", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (Dialog.Show(this, $"Uninstall these {chosen.Count} program(s), one after another?\n\n" +
                                  string.Join("\n", chosen.Take(15).Select(p => $"• {p.Name}")) +
                                  (chosen.Count > 15 ? $"\n… and {chosen.Count - 15} more" : "") +
                                  "\n\nPurgely can't undo what the programs' own uninstallers do.",
                "Related programs", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;
        Chosen.AddRange(chosen);
        DialogResult = true;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
