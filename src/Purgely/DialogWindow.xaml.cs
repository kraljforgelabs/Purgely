using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Purgely;

/// <summary>
/// Purgely's own message dialog in the app's Fluent style (replaces the old Windows MessageBox).
/// Same arguments and results as MessageBox, so calls can be switched one-to-one.
/// </summary>
public static class Dialog
{
    public static MessageBoxResult Show(Window? owner, string text, string caption = "Purgely",
                                        MessageBoxButton buttons = MessageBoxButton.OK,
                                        MessageBoxImage image = MessageBoxImage.None,
                                        MessageBoxResult defaultResult = MessageBoxResult.None,
                                        IReadOnlyDictionary<MessageBoxResult, string>? labels = null)
    {
        try
        {
            var dialog = new DialogWindow(text, caption, buttons, image, defaultResult, labels);
            if (owner is { IsVisible: true }) dialog.Owner = owner;
            else dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            dialog.ShowDialog();
            // A driver install or another program may have taken the focus meanwhile – bring Purgely back to the front.
            if (owner is { IsVisible: true }) owner.Activate();
            return dialog.Result;
        }
        catch (Exception)
        {
            // Never lose a question because of a styling problem.
            return owner is not null ? MessageBox.Show(owner, text, caption, buttons, image, defaultResult)
                                     : MessageBox.Show(text, caption, buttons, image, defaultResult);
        }
    }

    public static MessageBoxResult Show(string text, string caption = "Purgely",
                                        MessageBoxButton buttons = MessageBoxButton.OK,
                                        MessageBoxImage image = MessageBoxImage.None,
                                        MessageBoxResult defaultResult = MessageBoxResult.None) =>
        Show(null, text, caption, buttons, image, defaultResult);
}

public partial class DialogWindow : Window
{
    public MessageBoxResult Result { get; private set; } = MessageBoxResult.None;
    private readonly MessageBoxResult _cancelResult;

    public DialogWindow(string text, string caption, MessageBoxButton buttons, MessageBoxImage image, MessageBoxResult defaultResult,
                        IReadOnlyDictionary<MessageBoxResult, string>? labels = null)
    {
        InitializeComponent();
        Title = caption.StartsWith("Purgely", StringComparison.OrdinalIgnoreCase) ? caption : $"Purgely – {caption}";

        // First paragraph becomes the heading when the text has several, so the question stands out.
        var parts = text.Split("\n\n", 2, StringSplitOptions.None);
        if (parts.Length == 2 && parts[0].Length <= 160)
        {
            HeadingText.Text = parts[0].Trim();
            MessageText.Text = parts[1].Trim();
        }
        else
        {
            HeadingText.Visibility = Visibility.Collapsed;
            MessageText.Text = text.Trim();
        }

        (IconGlyph.Text, var brush) = image switch
        {
            MessageBoxImage.Error => ("", "AttentionBrush"),
            MessageBoxImage.Warning => ("", "AttentionBrush"),
            MessageBoxImage.Question => ("", "AccentBrush"),
            MessageBoxImage.Information => ("", "AccentBrush"),
            _ => ("", "AccentBrush"),
        };
        if (IconGlyph.Text.Length == 0) IconGlyph.Visibility = Visibility.Collapsed;
        else IconGlyph.Foreground = (Brush)FindResource(brush);

        var results = buttons switch
        {
            MessageBoxButton.YesNo => new[] { MessageBoxResult.Yes, MessageBoxResult.No },
            MessageBoxButton.YesNoCancel => new[] { MessageBoxResult.Yes, MessageBoxResult.No, MessageBoxResult.Cancel },
            MessageBoxButton.OKCancel => new[] { MessageBoxResult.OK, MessageBoxResult.Cancel },
            _ => new[] { MessageBoxResult.OK },
        };
        _cancelResult = results.Contains(MessageBoxResult.Cancel) ? MessageBoxResult.Cancel
                      : results.Contains(MessageBoxResult.No) ? MessageBoxResult.No : MessageBoxResult.OK;
        var def = results.Contains(defaultResult) ? defaultResult : results[0];

        foreach (var r in results)
        {
            var button = new Button
            {
                Content = labels is not null && labels.TryGetValue(r, out var label) ? label
                        : r switch { MessageBoxResult.Yes => "Yes", MessageBoxResult.No => "No", MessageBoxResult.Cancel => "Cancel", _ => "OK" },
                MinWidth = 96,
                Padding = new Thickness(16, 6, 16, 6),
                Margin = new Thickness(8, 0, 0, 0),
                IsDefault = r == def,
                IsCancel = r == _cancelResult,
            };
            // The safe/default choice gets the accent look only when it is a positive one (OK/Yes).
            if (r == def && r is MessageBoxResult.OK or MessageBoxResult.Yes && def == r)
                button.Style = (Style)FindResource("AccentButton");
            var result = r;
            button.Click += (_, _) => { Result = result; Close(); };
            ButtonsPanel.Children.Add(button);
            if (r == def) Loaded += (_, _) => button.Focus();
        }

        PreviewKeyDown += (_, e) =>
        {
            // Ctrl+C copies the message (handy for sending an error to someone).
            if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control)
            {
                try { Clipboard.SetText($"{HeadingText.Text}\n\n{MessageText.Text}".Trim()); } catch (Exception) { }
                e.Handled = true;
            }
        };
        Closed += (_, _) => { if (Result == MessageBoxResult.None) Result = _cancelResult; };
    }
}
