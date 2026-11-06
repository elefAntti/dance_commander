using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace DanceCommander.App.Views;

/// <summary>
/// Opens and closes a popup from the button that owns it.
/// </summary>
/// <remarks>
/// Clicking the button of an open <c>StaysOpen="False"</c> popup cannot be handled correctly,
/// because the button is not told about it: the open popup captures the mouse and swallows the
/// press, so the button's PreviewMouseLeftButtonDown never fires, while its Click still fires on
/// release. A handler therefore sees only half of the gesture, and whatever it recorded at press
/// time belongs to the *previous* click — which is why the popup closes and immediately reopens.
///
/// So the popup is set to stay open and dismissal is handled here instead. The button then always
/// sees the whole click and can simply toggle, and closing on a click elsewhere, on Escape, or on
/// the window losing focus is explicit rather than inferred.
/// </remarks>
internal sealed class PopupToggle
{
    private readonly Popup _popup;
    private readonly ButtonBase _button;
    private readonly Action _prepare;

    private PopupToggle(Popup popup, ButtonBase button, Action prepare)
    {
        _popup = popup;
        _button = button;
        _prepare = prepare;
    }

    public static void Attach(Popup popup, ButtonBase button, Action prepare)
    {
        var toggle = new PopupToggle(popup, button, prepare);

        // Ours to close now. Anything WPF would have done for us it also does behind our back.
        popup.StaysOpen = true;
        button.Click += (_, _) => toggle.Toggle();

        button.Loaded += (_, _) =>
        {
            if (Window.GetWindow(button) is not { } window)
                return;

            // Tunnelling, so it is seen before anything in the window can handle it. Clicks inside
            // the popup live in a different window and never arrive here, which is exactly right.
            window.PreviewMouseDown += (_, e) => toggle.CloseIfOutside(e.OriginalSource as DependencyObject);
            window.PreviewKeyDown += (_, e) =>
            {
                if (e.Key == Key.Escape && popup.IsOpen)
                {
                    popup.IsOpen = false;
                    e.Handled = true;
                }
            };
            window.Deactivated += (_, _) => popup.IsOpen = false;
        };
    }

    private void Toggle()
    {
        if (_popup.IsOpen)
        {
            _popup.IsOpen = false;
            return;
        }

        _prepare();
        // Only when the popup has no anchor of its own: the Activity popup is anchored to the whole
        // strip so it can't spill off the window, and moving it to the button undid exactly that.
        _popup.PlacementTarget ??= _button;
        _popup.IsOpen = true;
    }

    /// <summary>
    /// Closes on a click that came from neither the popup nor the button that owns it.
    /// </summary>
    /// <remarks>
    /// A popup with its own window is still part of the logical tree, so clicks inside it tunnel
    /// through this handler on the way down. Treating those as "outside" closes the popup on the
    /// mouse press, and the item under the pointer then never receives its Click on release — the
    /// popup appears to swallow the choice rather than act on it. The button is excluded for the
    /// opposite reason: it has its own handler, and closing here would let it reopen there.
    /// </remarks>
    private void CloseIfOutside(DependencyObject? clicked)
    {
        if (!_popup.IsOpen || clicked is null)
            return;

        for (var node = clicked; node is not null; node = Parent(node))
        {
            if (ReferenceEquals(node, _button) || ReferenceEquals(node, _popup) ||
                ReferenceEquals(node, _popup.Child))
            {
                return;
            }
        }

        _popup.IsOpen = false;
    }

    /// <summary>
    /// Up the visual tree, falling back to the logical one. The fallback is what carries the walk
    /// out of a popup's own visual tree and back to the Popup that owns it.
    /// </summary>
    private static DependencyObject? Parent(DependencyObject node) => TreeWalk.Parent(node);
}
