using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using System.ComponentModel;
using VMWV.Infrastructure.Windows.Globalization;
using VMWV.Infrastructure.Windows.Audio;
using VMWV.Infrastructure.Windows.Startup;
using VMWV.Infrastructure.Windows.Updates;
using VMWV.Infrastructure.Windows.Voicemeeter;
using VMWV_App.Localization;
using VMWV_App.ViewModels;

namespace VMWV_App;

public sealed partial class MainPage : Page
{
    private static readonly Lazy<MainPageViewModel> SharedViewModel = new(
        () => new MainPageViewModel(
            new WindowsAudioEndpointService(),
            new VoicemeeterRemoteClient(),
            new WindowsStartupService(),
            new GitHubReleaseUpdateService()));
    private static bool _sharedViewModelDisposed;

    private bool _layoutUpdateQueued;
    private double _lastLayoutWidth = -1;
    private bool? _lastNarrowLayout;
    private bool? _lastPaneOpen;
    private Storyboard? _paneRotationAnimation;
    private readonly Windows.UI.ViewManagement.UISettings _uiSettings = new();

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

    public static Task NotifySystemResumeAsync()
    {
        if (!SharedViewModel.IsValueCreated || _sharedViewModelDisposed)
        {
            return Task.CompletedTask;
        }

        SharedViewModel.Value.QueueSystemResume();
        return Task.CompletedTask;
    }

    public static async Task InitializeSharedViewModelAsync()
    {
        if (_sharedViewModelDisposed)
        {
            return;
        }

        await SharedViewModel.Value.InitializeAsync();
    }

    public static void RefreshRegionalFormats()
    {
        WindowsRegionalFormats.Refresh();
        if (SharedViewModel.IsValueCreated && !_sharedViewModelDisposed)
        {
            SharedViewModel.Value.RefreshRegionalFormats();
        }
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        ((LocalizationSource)Resources["Strings"]).Attach();
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        await InitializeSharedViewModelAsync();
        QueueResponsiveLayoutUpdate();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _paneRotationAnimation?.Stop();
        ((LocalizationSource)Resources["Strings"]).Detach();
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ViewModel.LayoutMode))
        {
            _lastLayoutWidth = -1;
            QueueResponsiveLayoutUpdate();
        }

        if (e.PropertyName == nameof(ViewModel.HideSupportPage) && ViewModel.HideSupportPage && SupportSection.Visibility == Visibility.Visible)
        {
            RootNavigation.SelectedItem = NavSettings;
            ShowSection("Settings");
        }

        if (e.PropertyName == nameof(ViewModel.Language))
        {
            UpdatePaneState();
        }
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
        DiagnosticsSection.Visibility = section == "Diagnostics" ? Visibility.Visible : Visibility.Collapsed;
        BindingsSection.Visibility = section == "Bindings" ? Visibility.Visible : Visibility.Collapsed;
        SettingsSection.Visibility = section == "Settings" ? Visibility.Visible : Visibility.Collapsed;
        SupportSection.Visibility = section == "Support" ? Visibility.Visible : Visibility.Collapsed;
        QueueResponsiveLayoutUpdate();
    }

    private void OnPaneToggleClicked(object sender, RoutedEventArgs e)
    {
        RootNavigation.IsPaneOpen = !RootNavigation.IsPaneOpen;
        UpdatePaneState();
        QueueResponsiveLayoutUpdate();
    }

    private async void OnOpenExternalLinkClicked(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string target }
            && Uri.TryCreate(target, UriKind.Absolute, out var uri))
        {
            await Windows.System.Launcher.LaunchUriAsync(uri);
        }
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
        PaneToggleButton.Width = isOpen ? double.NaN : 32;
        PaneToggleButton.HorizontalAlignment = isOpen ? HorizontalAlignment.Stretch : HorizontalAlignment.Center;

        var name = LocalizationService.Current.Get(isOpen ? "Navigation.Collapse" : "Navigation.Expand");
        AutomationProperties.SetName(PaneToggleButton, name);
        ToolTipService.SetToolTip(PaneToggleButton, name);

        if (_lastPaneOpen == isOpen) return;
        var fromAngle = PaneToggleRotation.Angle;
        _paneRotationAnimation?.Stop();
        var angle = isOpen ? 180 : 0;
        PaneToggleRotation.Angle = angle;
        if (_lastPaneOpen is not null && IsLoaded && _uiSettings.AnimationsEnabled)
        {
            var animation = new DoubleAnimation
            {
                From = fromAngle,
                To = angle,
                Duration = new Duration(TimeSpan.FromMilliseconds(200)),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
            };
            Storyboard.SetTarget(animation, PaneToggleRotation);
            Storyboard.SetTargetProperty(animation, nameof(PaneToggleRotation.Angle));
            _paneRotationAnimation = new Storyboard();
            _paneRotationAnimation.Children.Add(animation);
            _paneRotationAnimation.Begin();
        }
        _lastPaneOpen = isOpen;
    }

    private void OnContentRootSizeChanged(object sender, SizeChangedEventArgs e)
    {
        QueueResponsiveLayoutUpdate();
    }

    private void OnNavigationSizeChanged(object sender, SizeChangedEventArgs e)
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
        // Constrain the content by the window, not by a page's previous measured width.
        var paneWidth = RootNavigation.IsPaneOpen ? RootNavigation.OpenPaneLength : RootNavigation.CompactPaneLength;
        ContentRoot.Width = Math.Max(0, RootNavigation.ActualWidth - paneWidth);
        var width = Math.Max(0, ContentRoot.Width - ContentRoot.Padding.Left - ContentRoot.Padding.Right);
        if (Math.Abs(width - _lastLayoutWidth) < 1)
        {
            return;
        }

        _lastLayoutWidth = width;
        var contentWidth = EffectiveContentWidth(width, ViewModel.LayoutMode);
        SettingsContent.Width = contentWidth;
        SupportContent.Width = contentWidth;
        DashboardContent.Width = contentWidth;
        DiagnosticsContent.Width = contentWidth;
        BindingsSection.Width = contentWidth;
        var narrow = width < 760;
        if (_lastNarrowLayout == narrow)
        {
            return;
        }

        _lastNarrowLayout = narrow;

        BindingsStripColumn.Width = new GridLength(1, GridUnitType.Star);
        BindingsBusColumn.Width = narrow ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        BindingsPrimaryRow.Height = new GridLength(1, GridUnitType.Star);
        BindingsSecondaryRow.Height = narrow ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        Grid.SetColumn(BusBindingsCard, narrow ? 0 : 1);
        Grid.SetRow(BusBindingsCard, narrow ? 1 : 0);

    }

    private void OnDiagnosticsContentSizeChanged(object sender, SizeChangedEventArgs e)
    {
        DiagnosticsSummaryCard.MaxHeight = Math.Max(0, (e.NewSize.Height - 64) * 0.6);
    }

    private void OnDiagnosticsSummarySizeChanged(object sender, SizeChangedEventArgs e)
    {
        var narrow = e.NewSize.Width < 600;
        var blocks = new[] { DiagnosticsVoicemeeterBlock, DiagnosticsFailureBlock };
        var grid = (Grid)sender;
        var rows = narrow ? blocks.Length : 1;
        while (grid.RowDefinitions.Count > rows) grid.RowDefinitions.RemoveAt(grid.RowDefinitions.Count - 1);
        while (grid.RowDefinitions.Count < rows) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (var i = 0; i < blocks.Length; i++)
        {
            Grid.SetRow(blocks[i], narrow ? i : i / 2);
            Grid.SetColumn(blocks[i], narrow ? 0 : i % 2);
            Grid.SetColumnSpan(blocks[i], narrow ? 2 : 1);
        }
    }

    private void OnDashboardBindingsLoaded(object sender, RoutedEventArgs e) => UpdateDashboardBindingWidth(sender);

    private void OnDashboardBindingsSizeChanged(object sender, SizeChangedEventArgs e) => UpdateDashboardBindingWidth(sender);

    private static void UpdateDashboardBindingWidth(object sender)
    {
        if (sender is GridView { ActualWidth: > 0, ItemsPanelRoot: ItemsWrapGrid panel } view)
        {
            panel.ItemWidth = Math.Min(280, view.ActualWidth);
        }
    }

    private void OnSettingRowSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is not Grid grid || grid.ColumnDefinitions.Count < 2
            || grid.Children.LastOrDefault() is not FrameworkElement action) return;
        var count = grid.ColumnDefinitions.Count;
        var textColumn = count == 3 ? 1 : 0;
        var narrow = e.NewSize.Width < 600;
        if (grid.RowDefinitions.Count == 0)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }
        grid.RowSpacing = narrow ? 12 : 0;
        grid.ColumnDefinitions[count - 1].Width = narrow ? new GridLength(0)
            : count == 2 && SupportSection.Visibility == Visibility.Visible ? new GridLength(294) : GridLength.Auto;
        Grid.SetRow(action, narrow ? 1 : 0);
        Grid.SetColumn(action, narrow ? textColumn : count - 1);
        Grid.SetColumnSpan(action, narrow ? count - textColumn : 1);
        action.HorizontalAlignment = narrow ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        action.MaxWidth = Math.Max(0, e.NewSize.Width - (count == 3 ? 40 : 0));
    }

    private static double EffectiveContentWidth(double fallback, string layoutMode) =>
        layoutMode == "Expanded"
            ? Math.Max(0, fallback - 16)
            : Math.Min(1040, Math.Max(0, fallback - 16));

    public static Visibility BoolToVisibility(bool value) =>
        value ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility InverseBoolToVisibility(bool value) =>
        value ? Visibility.Collapsed : Visibility.Visible;

    public static string OnOffText(bool value) =>
        LocalizationService.Current.Get(value ? "Common.On" : "Common.Off");
}
