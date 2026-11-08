using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CinemaWatch.ViewModels;

namespace CinemaWatch.Views;

public partial class SeatsStepView : UserControl
{
    public SeatsStepView()
    {
        InitializeComponent();
    }

    // Hall map gestures. The rules live in CinemaWatch.Core (SeatSelection) and the state in HallMapViewModel; this
    // code only forwards the left mouse button in map units (the Grid inside the Viewbox, so scaling is handled).
    // A gesture starts with the press and ends with the release; Esc or a lost mouse capture (e.g. Alt+Tab)
    // cancels it and restores the selection from before the press.

    private void Map_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement map || map.DataContext is not HallMapViewModel vm) return;

        // Row numbers stay buttons: their click toggles the whole row.
        if ((e.OriginalSource as FrameworkElement)?.DataContext is RowLabelViewModel) return;

        var point = e.GetPosition(map);
        vm.BeginGesture(point.X, point.Y);
        map.Focus();

        // SubTree keeps the hover highlight working on the seats while the map holds the capture.
        if (!Mouse.Capture(map, CaptureMode.SubTree))
            vm.CancelGesture();

        // The seat buttons must not toggle themselves as well.
        e.Handled = true;
    }

    private void Map_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (sender is not FrameworkElement map || map.DataContext is not HallMapViewModel { IsGestureActive: true } vm) return;
        var point = e.GetPosition(map);
        vm.MoveGesture(point.X, point.Y);
    }

    private void Map_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement map || map.DataContext is not HallMapViewModel { IsGestureActive: true } vm) return;
        vm.EndGesture();
        map.ReleaseMouseCapture();
        e.Handled = true;
    }

    private void Map_LostMouseCapture(object sender, MouseEventArgs e)
    {
        // Only the map's own capture counts (the event bubbles up from the row-number buttons too).
        if (!ReferenceEquals(e.OriginalSource, sender)) return;
        if (sender is FrameworkElement { DataContext: HallMapViewModel vm })
            vm.CancelGesture();
    }

    private void Map_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        if (sender is not FrameworkElement map || map.DataContext is not HallMapViewModel { IsGestureActive: true } vm) return;
        vm.CancelGesture();
        map.ReleaseMouseCapture();
        e.Handled = true;
    }
}
