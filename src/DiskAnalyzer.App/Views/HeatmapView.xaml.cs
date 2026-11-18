using System.Windows.Controls;
using DiskAnalyzer.App.ViewModels;
using DiskAnalyzer.Core.Models;
using DiskAnalyzer.Visualization.Interaction;

namespace DiskAnalyzer.App.Views;

/// <summary>Forwards treemap control events to the view model (the control raises routed events, not commands).</summary>
public partial class HeatmapView : UserControl
{
    public HeatmapView()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            if (ViewModel is { } vm)
            {
                Treemap.LayoutOptions = new TreemapLayoutOptions { MinTilePixels = vm.MinTilePixels };
                Treemap.Refresh();
            }

            Treemap.Focus();
        };
    }

    private HeatmapViewModel? ViewModel => DataContext as HeatmapViewModel;

    private void Treemap_NodeClick(object? sender, TreemapNodeEventArgs e) => ViewModel?.OnTileClicked(e.TreeIndex, e.IsFolderLike);

    private void Treemap_ZoomRequested(object? sender, TreemapZoomEventArgs e) => ViewModel?.OnZoomRequested(e.ZoomIn, e.TreeIndex);

    private void Treemap_NodeRightClick(object? sender, TreemapNodeEventArgs e)
    {
        if (ViewModel is { } vm)
        {
            vm.SelectedNode = e.TreeIndex;
            TileMenu.IsOpen = true;
        }
    }

    private void Treemap_Rendered(object? sender, TreemapRenderedEventArgs e)
    {
        if (ViewModel is { } vm)
        {
            vm.RenderStats = $"{e.NodeCount:N0} tiles · layout {e.LayoutTime.TotalMilliseconds:0} ms · render {e.RenderTime.TotalMilliseconds:0} ms";
        }
    }
}
