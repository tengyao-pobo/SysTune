using System.Windows;

namespace SysTune.App.Controls;

/// <summary>Attached properties used by styles.</summary>
public static class Ui
{
    /// <summary>Marks an icon button as "on" (e.g. shuffle enabled) without the local-toggle semantics of ToggleButton.</summary>
    public static readonly DependencyProperty IsActiveProperty = DependencyProperty.RegisterAttached(
        "IsActive", typeof(bool), typeof(Ui), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public static bool GetIsActive(DependencyObject o) => (bool)o.GetValue(IsActiveProperty);

    public static void SetIsActive(DependencyObject o, bool value) => o.SetValue(IsActiveProperty, value);
}
