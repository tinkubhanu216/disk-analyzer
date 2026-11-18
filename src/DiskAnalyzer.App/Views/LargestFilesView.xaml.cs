using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DiskAnalyzer.App.ViewModels;

namespace DiskAnalyzer.App.Views;

public partial class LargestFilesView : UserControl
{
    public LargestFilesView() => InitializeComponent();

    private void List_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListView list && e.OriginalSource is DependencyObject source
            && ItemsControl.ContainerFromElement(list, source) is ListViewItem { Content: NodeItemViewModel item }
            && DataContext is LargestFilesViewModel vm)
        {
            vm.ShowLocationCommand.Execute(item);
        }
    }
}
