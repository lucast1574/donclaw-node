using DonClaw.Shared;
using DonClawTray.Services;
using Microsoft.UI.Xaml;
using WinUIEx;

namespace DonClawTray.Windows;

public sealed partial class SettingsWindow : WindowEx
{
    private readonly SettingsManager _settings;
    public bool IsClosed { get; private set; }

    public event EventHandler? SettingsSaved;

    public SettingsWindow(SettingsManager settings)
    {
        _settings = settings;
        InitializeComponent();

        Title = "DonClaw Node — Settings";

        this.SetWindowSize(460, 560);
        this.CenterOnScreen();

        LoadSettings();
        Closed += (s, e) => IsClosed = true;

        Logger.Info("[Settings] Window opened");
    }

    private void LoadSettings()
    {
        GatewayHostTextBox.Text = _settings.GatewayHost;
        TokenTextBox.Text = _settings.Token;
        AutoStartToggle.IsOn = _settings.AutoStart;
        NotificationsToggle.IsOn = _settings.ShowNotifications;
    }

    /// <summary>
    /// Show device/pairing info in the settings window.
    /// </summary>
    public void SetDeviceInfo(string? deviceId, string? pairingStatus)
    {
        if (string.IsNullOrEmpty(deviceId))
        {
            DeviceInfoPanel.Visibility = Visibility.Collapsed;
            return;
        }

        DeviceInfoPanel.Visibility = Visibility.Visible;
        DeviceIdLabel.Text = $"Device ID: {deviceId}";
        PairingStatusLabel.Text = pairingStatus ?? "";
    }

    public void ShowStatus(string message, bool isError = false)
    {
        StatusLabel.Text = message;
        StatusLabel.Visibility = Visibility.Visible;
        StatusLabel.Foreground = isError
            ? new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Red)
            : new Microsoft.UI.Xaml.Media.SolidColorBrush(
                Microsoft.UI.ColorHelper.FromArgb(255, 100, 100, 100));
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var gatewayHost = GatewayHostTextBox.Text.Trim();

        if (string.IsNullOrWhiteSpace(gatewayHost))
        {
            ShowStatus("❌ Gateway Host is required.", isError: true);
            return;
        }

        if (!GatewayUrlHelper.IsValidGatewayUrl(gatewayHost))
        {
            ShowStatus($"❌ {GatewayUrlHelper.ValidationMessage}", isError: true);
            return;
        }

        if (string.IsNullOrWhiteSpace(TokenTextBox.Text))
        {
            ShowStatus("❌ Token is required.", isError: true);
            return;
        }

        _settings.GatewayHost = gatewayHost;
        _settings.Token = TokenTextBox.Text.Trim();
        _settings.AutoStart = AutoStartToggle.IsOn;
        _settings.ShowNotifications = NotificationsToggle.IsOn;
        _settings.Save();

        AutoStartManager.SetAutoStart(_settings.AutoStart);

        Logger.Info("[Settings] Settings saved");
        SettingsSaved?.Invoke(this, EventArgs.Empty);
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        Logger.Info("[Settings] Cancel clicked");
        Close();
    }
}
