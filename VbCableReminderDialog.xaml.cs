using System.Diagnostics;
using System.Windows;

namespace KhurramAudioRoute;

public partial class VbCableReminderDialog
{
    public bool SuppressReminder => SuppressCheckBox.IsChecked == true;

    public VbCableReminderDialog()
    {
        InitializeComponent();
    }

    private void OnContinueClick(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }

    private void OnVbCableLinkClicked(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        Process.Start(new ProcessStartInfo("https://vb-audio.com/Cable/")
        {
            UseShellExecute = true
        });
    }
}
