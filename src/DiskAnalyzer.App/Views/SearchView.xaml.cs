using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DiskAnalyzer.App.ViewModels;

namespace DiskAnalyzer.App.Views;

public partial class SearchView : UserControl
{
    public SearchView() => InitializeComponent();

    private void Results_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source
            && ItemsControl.ContainerFromElement(ResultsList, source) is ListViewItem { Content: NodeItemViewModel item }
            && DataContext is SearchViewModel vm)
        {
            vm.ShowLocationCommand.Execute(item);
        }
    }
}
