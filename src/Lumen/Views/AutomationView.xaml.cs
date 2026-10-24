using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Lumen.Models;
using Lumen.Terminal;

namespace Lumen.Views;

public partial class AutomationView : UserControl
{
    private readonly SessionProfile _profile;

    public AutomationView(SessionProfile profile)
    {
        InitializeComponent();
        _profile = profile.Clone();
        ScriptHelpText.Text = AutomationScript.HelpText;
        ScriptBox.Text = _profile.Script;
        ScriptAutoRunCheck.IsChecked = _profile.ScriptAutoRun;
        ExpectBox.Text = AutomationTextParser.BuildExpectRules(_profile.ExpectRules);
        HighlightBox.Text = AutomationTextParser.BuildHighlightRules(_profile.HighlightRules);
        Loaded += (_, _) => ScriptBox.Focus();
    }

    public event Action<SessionProfile>? Saved;
    public event Action? Cancelled;

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        ErrorText.Text = "";

        if (!AutomationScript.TryParse(ScriptBox.Text, out _, out string scriptError))
        {
            ErrorText.Text = scriptError;
            ScriptBox.Focus();
            return;
        }

        if (!AutomationTextParser.TryParseExpectRules(ExpectBox.Text, out var expectRules, out string expectError))
        {
            ErrorText.Text = expectError;
            ExpectBox.Focus();
            return;
        }

        if (!AutomationTextParser.TryParseHighlightRules(HighlightBox.Text, out var highlightRules, out string highlightError))
        {
            ErrorText.Text = highlightError;
            HighlightBox.Focus();
            return;
        }

        _profile.Script = ScriptBox.Text.Trim();
        _profile.ScriptAutoRun = ScriptAutoRunCheck.IsChecked == true;
        _profile.ExpectRules = expectRules;
        _profile.HighlightRules = highlightRules;
        Saved?.Invoke(_profile.Clone());
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Cancelled?.Invoke();

    private void Root_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Cancelled?.Invoke();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            Save_Click(sender, e);
            e.Handled = true;
        }
    }
}
