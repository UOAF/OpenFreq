using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using OpenFreqClient.ViewModels;

namespace OpenFreqClient.Views;

public partial class ChannelCardListView : UserControl
{
    private const double CollapseBelow = 700;
    private const double ExpandAbove = 750;

    private bool _autoCollapsed;
    private Window? _parentWindow;

    public ChannelCardListView()
    {
        InitializeComponent();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _parentWindow = this.FindAncestorOfType<Window>();
        if (_parentWindow != null)
            _parentWindow.SizeChanged += OnWindowSizeChanged;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (_parentWindow != null)
            _parentWindow.SizeChanged -= OnWindowSizeChanged;
        _parentWindow = null;
    }

    private void OnWindowSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (DataContext is not ChannelCardListViewModel vm) return;

        if (e.NewSize.Width < CollapseBelow && vm.IsGroupPanelExpanded)
        {
            _autoCollapsed = true;
            vm.IsGroupPanelExpanded = false;
        }
        else if (e.NewSize.Width >= ExpandAbove && _autoCollapsed)
        {
            _autoCollapsed = false;
            vm.IsGroupPanelExpanded = true;
        }
        else if (e.NewSize.Width >= ExpandAbove)
        {
            _autoCollapsed = false;
        }
    }
}
