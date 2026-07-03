using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using VMWV.Infrastructure.Windows.Audio;
using VMWV.Infrastructure.Windows.Startup;
using VMWV.Infrastructure.Windows.Voicemeeter;
using VMWV_App.ViewModels;

namespace VMWV_App;

public sealed partial class MainPage : Page
{
    private static readonly Lazy<MainPageViewModel> SharedViewModel = new(
        () => new MainPageViewModel(new WindowsAudioEndpointService(), new VoicemeeterRemoteClient(), new WindowsStartupService()));
    private static bool _sharedViewModelDisposed;

    private bool _layoutUpdateQueued;
    private double _lastLayoutWidth = -1;
    private bool? _lastNarrowLayout;

    public MainPageViewModel ViewModel => SharedViewModel.Value;

    public MainPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        RootNavigation.SelectedItem = NavDashboard;
        UpdatePaneState();
        ShowSection("Dashboard");
    }

    public static async ValueTask DisposeSharedViewModelAsync()
    {
        if (!SharedViewModel.IsValueCreated || _sharedViewModelDisposed)
        {
            return;
        }

        _sharedViewModelDisposed = true;
        await SharedViewModel.Value.DisposeAsync();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await ViewModel.InitializeAsync();
        QueueResponsiveLayoutUpdate();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
    }

    private void OnNavigationSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is NavigationViewItem item && item.Tag is string tag)
        {
            ShowSection(tag);
        }
    }

    private void ShowSection(string section)
    {
        DashboardSection.Visibility = section == "Dashboard" ? Visibility.Visible : Visibility.Collapsed;
        BindingsSection.Visibility = section == "Bindings" ? Visibility.Visible : Visibility.Collapsed;
        SettingsSection.Visibility = section == "Settings" ? Visibility.Visible : Visibility.Collapsed;
        AboutSection.Visibility = section == "About" ? Visibility.Visible : Visibility.Collapsed;
        QueueResponsiveLayoutUpdate();
    }

    private void OnPaneToggleClicked(object sender, RoutedEventArgs e)
    {
        RootNavigation.IsPaneOpen = !RootNavigation.IsPaneOpen;
        UpdatePaneState();
    }

    private void OnNavigationPaneChanged(NavigationView sender, object args)
    {
        UpdatePaneState();
        QueueResponsiveLayoutUpdate();
    }

    private void UpdatePaneState()
    {
        var isOpen = RootNavigation.IsPaneOpen;
        var visibility = isOpen ? Visibility.Visible : Visibility.Collapsed;
        HeaderAppName.Visibility = visibility;
        PaneToggleText.Visibility = visibility;
        NavCompactLogo.Visibility = isOpen ? Visibility.Collapsed : Visibility.Visible;

        var name = isOpen ? "Collapse sidebar" : "Expand sidebar";
        AutomationProperties.SetName(PaneToggleButton, name);
        ToolTipService.SetToolTip(PaneToggleButton, name);
    }

    private void OnContentRootSizeChanged(object sender, SizeChangedEventArgs e)
    {
        QueueResponsiveLayoutUpdate();
    }

    private void QueueResponsiveLayoutUpdate()
    {
        if (_layoutUpdateQueued)
        {
            return;
        }

        _layoutUpdateQueued = true;
        if (App.DispatcherQueue is null)
        {
            _layoutUpdateQueued = false;
            UpdateResponsiveLayout();
            return;
        }

        App.DispatcherQueue.TryEnqueue(() =>
        {
            _layoutUpdateQueued = false;
            UpdateResponsiveLayout();
        });
    }

    private void UpdateResponsiveLayout()
    {
        var width = Math.Max(0, ContentRoot.ActualWidth - ContentRoot.Padding.Left - ContentRoot.Padding.Right);
        if (Math.Abs(width - _lastLayoutWidth) < 1)
        {
            return;
        }

        _lastLayoutWidth = width;
        var contentWidth = EffectiveContentWidth(width);
        SettingsContent.Width = EffectiveViewportWidth(SettingsSection, width);
        AboutContent.Width = EffectiveViewportWidth(AboutSection, width);
        DashboardContent.Width = contentWidth;
        BindingsSection.Width = contentWidth;
        var narrow = width < 760;
        if (_lastNarrowLayout == narrow)
        {
            return;
        }

        _lastNarrowLayout = narrow;

        DashboardStatusColumn.Width = new GridLength(1, GridUnitType.Star);
        DashboardBindingsColumn.Width = narrow ? new GridLength(0) : new GridLength(1.3, GridUnitType.Star);
        Grid.SetColumn(DashboardBindingsCardsGrid, narrow ? 0 : 1);
        Grid.SetRow(DashboardBindingsCardsGrid, narrow ? 1 : 0);

        BindingsStripColumn.Width = new GridLength(1, GridUnitType.Star);
        BindingsBusColumn.Width = narrow ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        BindingsPrimaryRow.Height = new GridLength(1, GridUnitType.Star);
        BindingsSecondaryRow.Height = narrow ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        Grid.SetColumn(BusBindingsCard, narrow ? 0 : 1);
        Grid.SetRow(BusBindingsCard, narrow ? 1 : 0);

        AboutModernColumn.Width = new GridLength(1, GridUnitType.Star);
        AboutLegacyColumn.Width = narrow ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        Grid.SetColumn(AboutLegacyCard, narrow ? 0 : 1);
        Grid.SetRow(AboutLegacyCard, narrow ? 1 : 0);
    }

    private static double EffectiveViewportWidth(ScrollViewer scrollViewer, double fallback)
    {
        var viewport = scrollViewer.ViewportWidth;
        if (!double.IsNaN(viewport) && !double.IsInfinity(viewport) && viewport > 0)
        {
            return Math.Max(0, viewport - 16);
        }

        return Math.Max(0, fallback - 16);
    }

    private static double EffectiveContentWidth(double fallback) => Math.Max(0, fallback - 16);

    public static Visibility BoolToVisibility(bool value) =>
        value ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility InverseBoolToVisibility(bool value) =>
        value ? Visibility.Collapsed : Visibility.Visible;

    public static string OnOffText(bool value) => value ? "On" : "Off";
}
