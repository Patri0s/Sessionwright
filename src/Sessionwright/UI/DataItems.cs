using System.Windows.Controls;

namespace Sessionwright;

/// <summary>
/// An ItemsControl whose items are always data, drawn with its template, also when an item is a visual itself: the open
/// terminals are windows, and a plain ItemsControl took each as its own row and tried to put the window into the list.
/// </summary>
public sealed class DataItems : ItemsControl
{
    protected override bool IsItemItsOwnContainerOverride(object item) => false;
}
