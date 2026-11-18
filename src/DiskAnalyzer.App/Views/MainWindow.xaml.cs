using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace DiskAnalyzer.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        FitToWorkArea();
        // Ctrl+F focuses the global search box.
        InputBindings.Add(new KeyBinding(new RelayKeyCommand(() =>
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
        }), Key.F, ModifierKeys.Control));
    }

    /// <summary>
    /// Keeps the window within the screen's working area (excluding the taskbar). On small or highly scaled
    /// displays the default size would otherwise exceed the screen and push the title bar off the top edge.
    /// </summary>
    private void FitToWorkArea()
    {
        Rect work = SystemParameters.WorkArea; // device-independent units, current DPI
        const double margin = 24;
        MinWidth = Math.Min(MinWidth, work.Width);
        MinHeight = Math.Min(MinHeight, work.Height);
        Width = Math.Min(Width, Math.Max(MinWidth, work.Width - (2 * margin)));
        Height = Math.Min(Height, Math.Max(MinHeight, work.Height - (2 * margin)));

        // Center within the work area explicitly so the title bar is always on screen.
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = work.Left + Math.Max(0, (work.Width - Width) / 2);
        Top = work.Top + Math.Max(0, (work.Height - Height) / 2);

        // Very small screens: start maximized rather than clipped.
        if (work.Width < 1100 || work.Height < 700)
        {
            WindowState = WindowState.Maximized;
        }
    }

    private void ExportButton_Click(object sender, RoutedEventArgs e)
    {
        if (ExportButton.ContextMenu is { } menu)
        {
            menu.PlacementTarget = ExportButton;
            menu.Placement = PlacementMode.Bottom;
            menu.IsOpen = true;
        }
    }

    private sealed class RelayKeyCommand(Action action) : ICommand
    {
        public event EventHandler? CanExecuteChanged
        {
            add { }
            remove { }
        }

        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter) => action();
    }
}
