using System.Windows;

namespace Sims4ModDoctor.Desktop;

public partial class DeleteConfirmationWindow : Window
{
    public DeleteConfirmationWindow()
    {
        InitializeComponent();
    }

    private void Cancel_Click(object sender, RoutedEventArgs eventArgs)
    {
        DialogResult = false;
    }

    private void Confirm_Click(object sender, RoutedEventArgs eventArgs)
    {
        DialogResult = true;
    }
}
