using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MediaCenter.App.Services;
using MediaCenter.Core;

namespace MediaCenter.App;

// Couch-Modus: Ein Controller (Xbox, PlayStation) steuert den Player und die Oberfläche. Im Player gilt ControllerPlayerMap;
// sonst bewegen Kreuz und linker Stick den Fokus, A/Kreuz öffnet, B/Kreis geht zurück, die Schultertasten wechseln den Reiter.
public partial class MainWindow
{
    private void InitController()
    {
        Services.Controller.Pressed += OnControllerPressed;
        Services.Controller.Start(ControllerService.IsForegroundWindowOurs);
    }

    private void OnControllerPressed(ControllerButton button)
    {
        if (PlayerActive)
        {
            if (ControllerPlayerMap.For(button) is { } mapped && RunPlayerAction(mapped.Action, mapped.Large)) _lastActivity = DateTime.UtcNow;
            return;
        }

        FrameworkElement? layer = Dialog.IsOpen ? Dialog : Modal.IsOpen ? Modal : null;
        switch (button)
        {
            case ControllerButton.DpadUp: MoveFocus(FocusNavigationDirection.Up, layer); break;
            case ControllerButton.DpadDown: MoveFocus(FocusNavigationDirection.Down, layer); break;
            case ControllerButton.DpadLeft: MoveFocus(FocusNavigationDirection.Left, layer); break;
            case ControllerButton.DpadRight: MoveFocus(FocusNavigationDirection.Right, layer); break;
            case ControllerButton.A: ActivateFocused(layer); break;
            case ControllerButton.B: GoBack(layer); break;
            case ControllerButton.X: OpenFocusedMenu(); break;
            case ControllerButton.Y when layer is null: OpenSearch(); break;
            case ControllerButton.LeftBumper when layer is null: StepSection(-1); break;
            case ControllerButton.RightBumper when layer is null: StepSection(1); break;
            case ControllerButton.LeftTrigger: ScrollPage(-1, layer); break;
            case ControllerButton.RightTrigger: ScrollPage(1, layer); break;
            case ControllerButton.Menu: ToggleFullscreen(); break;
            case ControllerButton.View when layer is null: Navigate(Section.Home); break;
        }
    }

    private static bool IsInside(DependencyObject? element, DependencyObject ancestor)
    {
        for (var current = element; current is not null; current = current is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current))
        {
            if (current == ancestor) return true;
        }
        return false;
    }

    private void MoveFocus(FocusNavigationDirection direction, FrameworkElement? layer)
    {
        var root = layer ?? (FrameworkElement)this;
        if (Keyboard.FocusedElement is not UIElement { IsVisible: true } element || !IsInside(element, root))
        {
            FocusFirst(layer);
            return;
        }

        if (!element.MoveFocus(new TraversalRequest(direction))) return;
        if (layer is not null && !IsInside(Keyboard.FocusedElement as DependencyObject, layer))
        {
            element.Focus(); // das Fenster vor der Seite nicht verlassen
            return;
        }
        (Keyboard.FocusedElement as FrameworkElement)?.BringIntoView();
    }

    /// <summary>Setzt den Fokus auf das erste bedienbare Element des offenen Fensters oder der Seite.</summary>
    private void FocusFirst(FrameworkElement? layer)
    {
        if ((layer ?? PageHost.Content as UIElement) is not { } target) return;

        target.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
        (Keyboard.FocusedElement as FrameworkElement)?.BringIntoView();
    }

    private void ActivateFocused(FrameworkElement? layer)
    {
        if (Keyboard.FocusedElement is not UIElement element || !IsInside(element, layer ?? (FrameworkElement)this))
        {
            FocusFirst(layer);
            return;
        }

        var peer = UIElementAutomationPeer.CreatePeerForElement(element);
        if (peer?.GetPattern(PatternInterface.Invoke) is IInvokeProvider invoke) invoke.Invoke();
        else if (peer?.GetPattern(PatternInterface.SelectionItem) is ISelectionItemProvider selection) selection.Select();
        else if (peer?.GetPattern(PatternInterface.Toggle) is IToggleProvider toggle) toggle.Toggle();
        else if (peer?.GetPattern(PatternInterface.ExpandCollapse) is IExpandCollapseProvider expand) expand.Expand();
        else RaiseKey(element, Key.Enter);
    }

    /// <summary>Menü der Karte oder Zeile im Fokus (wie Rechtsklick oder Menütaste).</summary>
    private void OpenFocusedMenu()
    {
        if (Keyboard.FocusedElement is UIElement element) RaiseKey(element, Key.Apps);
    }

    private static void RaiseKey(UIElement element, Key key)
    {
        var source = PresentationSource.FromVisual(element);
        if (source is null) return;

        element.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, key) { RoutedEvent = Keyboard.KeyDownEvent });
        element.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, key) { RoutedEvent = Keyboard.KeyUpEvent });
    }

    /// <summary>Wie Esc: ein offenes Fenster schließen, sonst eine Seite zurück.</summary>
    private void GoBack(FrameworkElement? layer)
    {
        if (layer is Controls.ModalHost host)
        {
            host.TryClose();
            return;
        }
        if (_history.Count > 0) Back();
    }

    /// <summary>Die Hauptreiter (Start bis Podcasts) der Reihe nach; am Ende geht es von vorn los.</summary>
    private void StepSection(int direction)
    {
        var sections = new[] { Section.Home, Section.Movies, Section.Series, Section.Music, Section.Radio, Section.Live, Section.Podcasts }
            .Where(s => _navButtons.TryGetValue(s, out var button) && button.Visibility == Visibility.Visible).ToList();
        if (sections.Count == 0) return;

        var current = sections.FindIndex(s => _navButtons[s].IsChecked == true);
        var next = current < 0 ? 0 : (current + direction + sections.Count) % sections.Count;
        Navigate(sections[next]);
    }

    /// <summary>Blättert die Seite (oder das Fenster) um fast eine Bildschirmhöhe.</summary>
    private void ScrollPage(int direction, FrameworkElement? layer)
    {
        if (FindScrollViewer(layer ?? PageHost.Content as DependencyObject) is not { } scroll) return;

        scroll.ScrollToVerticalOffset(Math.Clamp(scroll.VerticalOffset + direction * scroll.ViewportHeight * 0.8, 0, scroll.ScrollableHeight));
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject? root)
    {
        if (root is null) return null;
        if (root is ScrollViewer { ScrollableHeight: > 0 } found) return found;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            if (FindScrollViewer(VisualTreeHelper.GetChild(root, i)) is { } inner) return inner;
        }
        return null;
    }
}
