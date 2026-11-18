using System.Windows.Controls;
using System.Windows.Input;
using DiskAnalyzer.App.ViewModels;

namespace DiskAnalyzer.App.Views;

public partial class HistoryView : UserControl
{
    public HistoryView() => InitializeComponent();

    private void Scans_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is HistoryViewModel vm && vm.SelectedScan is { } row && e.OriginalSource is not CheckBox)
        {
            vm.OpenCommand.Execute(row);
        }
    }
}
