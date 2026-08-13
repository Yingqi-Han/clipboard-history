using Wpf.Ui.Controls;

namespace YingqiClipboard;

public partial class ClipboardCompactWindow : FluentWindow
{
    public ClipboardCompactWindow(ClipboardHistorySession session, bool topmost)
    {
        InitializeComponent();
        Topmost = topmost;
        ControlHost.Content = new ClipboardHistoryControl(session, ClipboardHistoryDisplayMode.CompactWindow);
    }

    public event EventHandler<bool>? TopmostPreferenceChanged;

    private void TopmostToggle_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        Topmost = TopmostToggle.IsChecked == true;
        TopmostPreferenceChanged?.Invoke(this, Topmost);
    }
}
