using System.Windows.Controls;
using DiskAnalyzer.App.ViewModels;
using DiskAnalyzer.Visualization.Interaction;

namespace DiskAnalyzer.App.Views;

public partial class OverviewView : UserControl
{
    public OverviewView() => InitializeComponent();

    private void MiniTreemap_NodeClick(object? sender, TreemapNodeEventArgs e)
    {
        if (DataContext is OverviewViewModel vm)
        {
            vm.OnTreemapClick(e.TreeIndex);
        }
    }
}
