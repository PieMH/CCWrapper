using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CCWrapper.Core;

namespace CCWrapper.Views;

/// <summary>The center's tab bar: one card per open session (status, name, close), the read-only card of a session open in
/// another terminal, New session and search. Cards are dragged to reorder.</summary>
public partial class SessionTabs : UserControl
{
    public static readonly DependencyProperty CardWidthProperty =
        DependencyProperty.Register(nameof(CardWidth), typeof(double), typeof(SessionTabs), new PropertyMetadata(ShellModel.MaxCardWidth));

    /// <summary>Every card's width: they share the bar, then scroll sideways (see <see cref="ShellModel.CardWidth"/>).</summary>
    public double CardWidth { get => (double)GetValue(CardWidthProperty); set => SetValue(CardWidthProperty, value); }

    MainWindow Main => (MainWindow)Window.GetWindow(this);
    AppState State => AppState.Current;
    bool _syncing;
    Point _downAt;
    LiveSession? _pressed;

    public SessionTabs()
    {
        InitializeComponent();
        Cards.ItemsSource = State.OpenSessions;
        State.OpenSessions.CollectionChanged += (_, _) => Layout();
        State.PropertyChanged += OnStateChanged;
        SizeChanged += (_, _) => Layout();
    }

    /// <summary>MainWindow calls this when the read-only view changes: its card, and whether it is the one shown.</summary>
    public void ShowReadOnly(string? title, bool active)
    {
        ReadOnlyCard.Visibility = title is null ? Visibility.Collapsed : Visibility.Visible;
        ReadOnlyTitle.Text = title ?? "";
        ReadOnlyBg.Background = active ? (System.Windows.Media.Brush)FindResource("Raised") : System.Windows.Media.Brushes.Transparent;
        ReadOnlyUnderline.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        SearchButton.IsEnabled = State.ActiveSession is not null || active;
        Layout();
    }

    void OnStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(AppState.ActiveSession)) return;
        _syncing = true;
        Cards.SelectedItem = State.ActiveSession;
        _syncing = false;
        SearchButton.IsEnabled = State.ActiveSession is not null || ReadOnlyCard.IsVisible;
        if (State.ActiveSession is { } s && Cards.ItemContainerGenerator.ContainerFromItem(s) is FrameworkElement card) card.BringIntoView();
    }

    void Layout() => CardWidth = ShellModel.CardWidth(Scroller.ActualWidth - NewButton.ActualWidth - 8,
        State.OpenSessions.Count + (ReadOnlyCard.Visibility == Visibility.Visible ? 1 : 0));

    void OnSelect(object sender, SelectionChangedEventArgs e)
    {
        if (!_syncing && Cards.SelectedItem is LiveSession s && s != State.ActiveSession) Main.Activate(s);
    }

    void OnCloseCard(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: LiveSession s } && Main.ConfirmEnd("Closing this session", [s])) Main.CloseSession(s);
    }

    // Delete is always refused here (the tab has the session open); the sidebar row offers it once the tab is closed.
    void OnCardMenu(object sender, ContextMenuEventArgs e)
    {
        if (sender is not ListBoxItem { DataContext: LiveSession s } card) return;
        e.Handled = true;
        SessionMenu.Open(card, s, null, s.Title ?? "New session", null, () => { });
    }

    void OnReadOnlyClick(object sender, MouseButtonEventArgs e) => Main.ShowReadOnlyCard();
    void OnCloseReadOnly(object sender, RoutedEventArgs e) => Main.CloseElsewhere();
    async void OnNew(object sender, RoutedEventArgs e) => await Main.NewSessionAsync();
    void OnSearch(object sender, RoutedEventArgs e) => Main.OpenFind();

    void OnWheel(object sender, MouseWheelEventArgs e)
    {
        Scroller.ScrollToHorizontalOffset(Scroller.HorizontalOffset - e.Delta);
        e.Handled = true;
    }

    // ---- drag to reorder: the dragged card takes the place of the one under the pointer as it moves, as browsers do ----

    void OnCardDown(object sender, MouseButtonEventArgs e)
    {
        _downAt = e.GetPosition(this);
        _pressed = (sender as FrameworkElement)?.DataContext as LiveSession;
    }

    void OnCardMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _pressed is not { } dragged) return;
        if (Math.Abs(e.GetPosition(this).X - _downAt.X) < SystemParameters.MinimumHorizontalDragDistance) return;
        _pressed = null;
        DragDrop.DoDragDrop((DependencyObject)sender, new DataObject(typeof(LiveSession), dragged), DragDropEffects.Move);
    }

    void OnCardDragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        e.Effects = DragDropEffects.None;
        if (e.Data.GetData(typeof(LiveSession)) is not LiveSession dragged || (sender as FrameworkElement)?.DataContext is not LiveSession target) return;
        e.Effects = DragDropEffects.Move;
        var open = State.OpenSessions;
        int from = open.IndexOf(dragged), to = open.IndexOf(target);
        if (from >= 0 && to >= 0 && from != to) open.Move(from, to);
    }

    void OnCardDrop(object sender, DragEventArgs e) => e.Handled = true;
}
