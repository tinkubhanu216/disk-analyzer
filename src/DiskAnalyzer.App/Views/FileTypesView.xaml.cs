using System.Windows.Controls;
using System.Windows.Input;
using DiskAnalyzer.App.ViewModels;

namespace DiskAnalyzer.App.Views;

public partial class FileTypesView : UserControl
{
    public FileTypesView() => InitializeComponent();

    private void Extensions_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is FileTypesViewModel vm && vm.SelectedExtension is { } row)
        {
            vm.SearchExtensionCommand.Execute(row);
        }
    }
}
