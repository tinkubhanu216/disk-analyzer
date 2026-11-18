using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using DiskAnalyzer.App.ViewModels;

namespace DiskAnalyzer.App.Views;

public partial class ExplorerView : UserControl
{
    public ExplorerView()
    {
        InitializeComponent();
        Loaded += (_, _) => ApplyTreeItemStyle();
        PreviewKeyDown += OnPreviewKeyDown;
    }

    /// <summary>
    /// Binds TreeViewItem.IsExpanded/IsSelected to the lazy view models. The style is built at runtime so it can be
    /// based on the active (Fluent) TreeViewItem style instead of replacing it.
    /// </summary>
    private void ApplyTreeItemStyle()
    {
        var style = new Style(typeof(TreeViewItem), TryFindResource(typeof(TreeViewItem)) as Style);
        style.Setters.Add(new Setter(TreeViewItem.IsExpandedProperty, new Binding(nameof(FolderTreeItemViewModel.IsExpanded)) { Mode = BindingMode.TwoWay }));
        style.Setters.Add(new Setter(TreeViewItem.IsSelectedProperty, new Binding(nameof(FolderTreeItemViewModel.IsSelected)) { Mode = BindingMode.TwoWay }));
        FolderTree.ItemContainerStyle = style;
    }

    private ExplorerViewModel? ViewModel => DataContext as ExplorerViewModel;

    private void ItemsList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && ItemsControl.ContainerFromElement(ItemsList, source) is ListViewItem { Content: NodeItemViewModel item })
        {
            ViewModel?.OpenCommand.Execute(item);
        }
    }

    private void ItemsList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && ItemsList.SelectedItem is NodeItemViewModel item)
        {
            ViewModel?.OpenCommand.Execute(item);
            e.Handled = true;
        }
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (ViewModel is null || e.OriginalSource is TextBox)
        {
            return;
        }

        if (e.Key == Key.Back || (e.Key == Key.Up && Keyboard.Modifiers == ModifierKeys.Alt))
        {
            ViewModel.GoUpCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.SystemKey == Key.Left && Keyboard.Modifiers == ModifierKeys.Alt)
        {
            ViewModel.GoBackCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.SystemKey == Key.Right && Keyboard.Modifiers == ModifierKeys.Alt)
        {
            ViewModel.GoForwardCommand.Execute(null);
            e.Handled = true;
        }
    }
}
