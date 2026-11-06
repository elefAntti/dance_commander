using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace DanceCommander.App.Views;

/// <summary>
/// Walking up from whatever a mouse event happened to land on.
/// </summary>
/// <remarks>
/// WPF has two trees and <see cref="VisualTreeHelper.GetParent"/> throws rather than returns null
/// when handed something that is only in the other one. Both things a pane routinely lands on are
/// like that: a <c>Run</c> inside a <c>TextBlock</c> is a content element, and a control inside a
/// popup sits in a visual tree of its own. Every walk in the application goes through here so that
/// neither has to be remembered again -- the first was found by a popup dismissing wrongly, and the
/// second by the whole application crashing on a click.
/// </remarks>
internal static class TreeWalk
{
    /// <summary>The visual parent, or the logical one when there is no visual to ask about.</summary>
    public static DependencyObject? Parent(DependencyObject node)
    {
        var visual = node is Visual or Visual3D ? VisualTreeHelper.GetParent(node) : null;
        return visual ?? LogicalTreeHelper.GetParent(node);
    }

    /// <summary>The first visual descendant of this type, depth first, or null.</summary>
    public static T? Descendant<T>(DependencyObject node) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
        {
            var child = VisualTreeHelper.GetChild(node, i);
            if (child is T found)
                return found;
            if (Descendant<T>(child) is { } deeper)
                return deeper;
        }

        return null;
    }

    /// <summary>The nearest ancestor of this type, including the node itself, or null.</summary>
    public static T? Ancestor<T>(DependencyObject? node) where T : DependencyObject
    {
        while (node is not null and not T)
            node = Parent(node);

        return node as T;
    }

    /// <summary>
    /// Whether the node is the ancestor or under it. <see cref="Visual.IsAncestorOf"/> throws when
    /// handed a content element, so one is judged by the visual it sits in.
    /// </summary>
    public static bool Within(Visual ancestor, DependencyObject? node) =>
        Ancestor<Visual>(node) is { } visual && ancestor.IsAncestorOf(visual);
}
