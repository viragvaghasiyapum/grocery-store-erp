using System.Windows;
using System.Windows.Controls;

namespace GroceryErp.App.Presentation;

public sealed class AnimatedSection : HeaderedContentControl
{
    public static readonly DependencyProperty IsExpandedProperty = DependencyProperty.Register(
        nameof(IsExpanded), typeof(bool), typeof(AnimatedSection),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            static (dependency, _) => ((AnimatedSection)dependency).UpdateContent()));

    private FrameworkElement? _contentHost;

    public bool IsExpanded
    {
        get => (bool)GetValue(IsExpandedProperty);
        set => SetValue(IsExpandedProperty, value);
    }

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        _contentHost = GetTemplateChild("PART_ContentHost") as FrameworkElement;
        if (_contentHost is not null)
        {
            _contentHost.Visibility = Visibility.Collapsed;
            SectionMotion.SetOpen(_contentHost, IsExpanded);
        }
    }

    private void UpdateContent()
    {
        if (_contentHost is not null) SectionMotion.SetOpen(_contentHost, IsExpanded);
    }
}
