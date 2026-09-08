using System.ComponentModel;
using System.Data.Common;
using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using PhoneTransfer.Application.Text;
using PhoneTransfer.Domain;
using PhoneTransfer.Host;

namespace PhoneTransfer.App;

internal sealed class ServerWindow : Form
{
    private WindowsServerRuntime? runtime;
    private readonly CancellationTokenSource lifetime = new();
    private readonly string dataDirectory;
    private readonly WindowsShareConfiguration shareConfiguration;
    private readonly Label status = new() { Text = "起動中…", AutoSize = true };
    private readonly ComboBox networks = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 240, DisplayMember = "Name" };
    private readonly Button reconnect = new() { Text = "接続をやり直す", AutoSize = true };
    private readonly Button pair = new() { Text = "スマホを登録", AutoSize = true, Enabled = false };
    private readonly Button revoke = new() { Text = "選択したスマホの登録を解除", AutoSize = true, Enabled = false };
    private readonly ListBox devices = new() { Dock = DockStyle.Fill, DisplayMember = "DisplayName" };
    private readonly Label shareStatus = new() { AutoSize = true, MaximumSize = new Size(640, 0) };
    private readonly Button chooseShare = new() { Text = "受信フォルダを選択", AutoSize = true };
    private readonly Button clearShare = new() { Text = "受信フォルダ設定を解除", AutoSize = true, Enabled = false };
    private readonly Label receivedTextStatus = new()
    {
        Text = "スマホから受信したテキスト/URL: まだありません",
        AutoSize = true,
        MaximumSize = new Size(640, 0)
    };
    private readonly TextBox receivedText = new()
    {
        ReadOnly = true,
        Multiline = true,
        ScrollBars = ScrollBars.Vertical,
        Dock = DockStyle.Fill
    };
    private readonly Button copyReceivedText = new() { Text = "コピー", AutoSize = true, Enabled = false };
    private readonly Button openReceivedUrl = new() { Text = "URLを開く", AutoSize = true, Enabled = false };
    private ReceivedTextMessage? latestText;
    private bool closing;
    private bool starting;
    private bool resourcesDisposed;

    public event Action? TextReceived;

    public ServerWindow()
    {
        dataDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PhoneTransfer");
        shareConfiguration = new WindowsShareConfiguration(dataDirectory);

        Text = "Phone Transfer";
        ClientSize = new Size(700, 650);
        MinimumSize = new Size(560, 520);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(20), RowCount = 11, ColumnCount = 1 };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(status, 0, 0);
        var connection = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        connection.Controls.AddRange([networks, reconnect]);
        layout.Controls.Add(connection, 0, 1);
        layout.Controls.Add(pair, 0, 2);
        layout.Controls.Add(devices, 0, 3);
        layout.Controls.Add(revoke, 0, 4);
        layout.Controls.Add(shareStatus, 0, 5);
        var shareActions = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        shareActions.Controls.AddRange([chooseShare, clearShare]);
        layout.Controls.Add(shareActions, 0, 6);
        layout.Controls.Add(receivedTextStatus, 0, 7);
        layout.Controls.Add(receivedText, 0, 8);
        var textActions = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        textActions.Controls.AddRange([copyReceivedText, openReceivedUrl]);
        layout.Controls.Add(textActions, 0, 9);
        layout.Controls.Add(new Label
        {
            AutoSize = true,
            Text = "ファイル転送とAndroid→PCのテキスト/URL送信APIは有効です。\n閉じるとトレイで待機します。 App 0.1.0 / Build 1 / Protocol 1"
        }, 0, 10);
        Controls.Add(layout);
        Shown += async (_, _) =>
        {
            RefreshShareConfiguration();
            await StartAsync();
        };
        reconnect.Click += async (_, _) => await StartAsync();
        chooseShare.Click += (_, _) => ChooseShare();
        clearShare.Click += (_, _) => ClearShare();
        copyReceivedText.Click += (_, _) => CopyLatestText();
        openReceivedUrl.Click += (_, _) => OpenLatestUrl();
        pair.Click += async (_, _) =>
        {
            if (runtime is null) return;
            try
            {
                using var dialog = new PairingDialog(runtime);
                dialog.ShowDialog(this);
                await RefreshDevicesAsync();
            }
            catch (Exception exception) when (exception is IOException or DbException or CryptographicException or UnauthorizedAccessException)
            {
                status.Text = "登録画面を準備できませんでした。接続をやり直してください。";
            }
        };
        devices.SelectedIndexChanged += (_, _) => revoke.Enabled = runtime is not null && devices.SelectedItem is PairedDevice;
        revoke.Click += async (_, _) =>
        {
            if (runtime is null || devices.SelectedItem is not PairedDevice device) return;
            if (MessageBox.Show(this, $"{device.DisplayName} の登録を解除しますか？", "登録解除",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            try
            {
                await runtime.RevokeAsync(device.DeviceId);
                await RefreshDevicesAsync();
            }
            catch (Exception exception) when (exception is IOException or DbException or UnauthorizedAccessException)
            {
                status.Text = "登録を解除できませんでした。PCの保存先を確認してください。";
            }
        };
        FormClosing += (_, args) =>
        {
            if (!closing && args.CloseReason == CloseReason.UserClosing) { args.Cancel = true; Hide(); }
        };
    }

    private void ChooseShare()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "スマホから受信するフォルダを選択してください。ペアリング済み端末の基本ファイルAPIで使用します。",
            ShowNewFolderButton = true,
            UseDescriptionForTitle = true
        };
        try
        {
            var current = shareConfiguration.GetRootPath();
            if (current is not null) dialog.SelectedPath = current;
        }
        catch (Exception exception) when (IsShareConfigurationException(exception))
        {
            // An invalid old setting must not prevent choosing a replacement folder.
        }

        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            shareConfiguration.SetRootPath(dialog.SelectedPath);
            RefreshShareConfiguration();
            status.Text = "受信フォルダ設定を保存しました。新しいファイルAPI要求に反映されます。";
        }
        catch (Exception exception) when (IsShareConfigurationException(exception))
        {
            MessageBox.Show(this,
                "NTFS/ReFSのローカルフォルダを選択してください。ネットワーク共有、ドライブ直下、リンク/ジャンクション、アプリ自身の保存先は使用できません。",
                "受信フォルダを設定できません",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
    }

    private void ClearShare()
    {
        try
        {
            shareConfiguration.Clear();
            RefreshShareConfiguration();
            status.Text = "受信フォルダ設定を解除しました。進行中のアップロードは次の書き込み時に中止されます。";
        }
        catch (Exception exception) when (IsShareConfigurationException(exception))
        {
            status.Text = "受信フォルダ設定を解除できませんでした。PCの保存先を確認してください。";
        }
    }

    private void RefreshShareConfiguration()
    {
        try
        {
            var root = shareConfiguration.GetRootPath();
            shareStatus.Text = root is null
                ? "受信フォルダ: 未設定（ファイルAPIでは共有なしとして扱います）"
                : $"受信フォルダ: {root}\n（ペアリング済み端末向けファイルAPIで使用中）";
            clearShare.Enabled = root is not null;
        }
        catch (Exception exception) when (IsShareConfigurationException(exception))
        {
            shareStatus.Text = "受信フォルダ設定を読み込めません。再選択するか設定を解除してください。";
            clearShare.Enabled = true;
        }
    }

    private static bool IsShareConfigurationException(Exception exception) =>
        exception is IOException or InvalidDataException or UnauthorizedAccessException or NotSupportedException or ArgumentException;

    private async Task StartAsync()
    {
        if (starting) return;
        starting = true;
        pair.Enabled = reconnect.Enabled = revoke.Enabled = false;
        status.Text = "接続を準備しています…";
        try
        {
            await StopAsync();
            devices.DataSource = null;
            var selected = (networks.SelectedItem as WindowsLanAdapter)?.Address;
            var adapters = WindowsLanAdapters.Find();
            networks.DataSource = adapters.ToList();
            if (selected is not null)
                networks.SelectedItem = adapters.FirstOrDefault(adapter => adapter.Address.Equals(selected)) ?? adapters.FirstOrDefault();
            if (networks.SelectedItem is not WindowsLanAdapter adapter)
            {
                status.Text = "LANに接続されていません。Wi-Fiまたは有線LANを接続してください。";
                return;
            }
            var started = await WindowsServerRuntime.StartAsync(dataDirectory, adapter.Address, lifetime.Token, HandleTextReceived);
            if (closing || IsDisposed) { await started.DisposeAsync(); return; }
            runtime = started;
            status.Text = started.MdnsAvailable
                ? "スマホからの接続を待っています。"
                : "接続を開始しました。自動検出できない場合はQRで登録してください。";
            pair.Enabled = true;
            await RefreshDevicesAsync();
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception exception) when (exception is IOException or DbException or CryptographicException or
            SocketException or NetworkInformationException or FormatException or UnauthorizedAccessException or InvalidOperationException)
        {
            status.Text = "起動できませんでした。LAN接続・PCの保存先・ほかの起動中アプリを確認してください。";
        }
        finally { starting = false; if (!IsDisposed && !closing) reconnect.Enabled = true; }
    }

    private void HandleTextReceived(ReceivedTextMessage message)
    {
        if (closing || IsDisposed || !IsHandleCreated) return;
        if (InvokeRequired)
        {
            try { BeginInvoke(new Action(() => HandleTextReceived(message))); }
            catch (InvalidOperationException) { }
            return;
        }

        latestText = message;
        var kind = message.Entry.Kind == "url" ? "URL" : "テキスト";
        receivedTextStatus.Text = $"{message.SourceDisplayName} から{kind}を受信しました";
        receivedText.Text = message.Entry.Content;
        copyReceivedText.Enabled = true;
        openReceivedUrl.Enabled = message.Entry.Kind == "url" && TryGetSafeUrl(message.Entry.Content, out _);
        TextReceived?.Invoke();
    }

    private void CopyLatestText()
    {
        var content = latestText?.Entry.Content;
        if (string.IsNullOrEmpty(content)) return;
        try
        {
            Clipboard.SetText(content);
            status.Text = "受信した内容をクリップボードへコピーしました。";
        }
        catch (ExternalException)
        {
            status.Text = "クリップボードを使用できませんでした。もう一度お試しください。";
        }
    }

    private void OpenLatestUrl()
    {
        var content = latestText?.Entry.Content;
        if (content is null || !TryGetSafeUrl(content, out var uri)) return;
        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            status.Text = "URLを開けませんでした。既定のブラウザー設定を確認してください。";
        }
    }

    private static bool TryGetSafeUrl(string content, out Uri uri)
    {
        if (Uri.TryCreate(content, UriKind.Absolute, out var candidate) &&
            (candidate.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase) ||
             candidate.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase)) &&
            !string.IsNullOrWhiteSpace(candidate.Host) && string.IsNullOrEmpty(candidate.UserInfo))
        {
            uri = candidate;
            return true;
        }
        uri = null!;
        return false;
    }

    private async Task RefreshDevicesAsync()
    {
        if (runtime is null) return;
        var result = await runtime.GetDevicesAsync();
        if (!IsDisposed && !closing) devices.DataSource = result.Where(device => !device.Revoked).ToList();
    }

    public async Task StopAsync()
    {
        var previous = runtime;
        runtime = null;
        if (previous is not null) await previous.DisposeAsync();
    }

    public async Task CloseApplicationAsync()
    {
        if (closing) return;
        closing = true;
        lifetime.Cancel();
        try { await StopAsync(); }
        finally { Close(); }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !resourcesDisposed)
        {
            resourcesDisposed = true;
            lifetime.Cancel();
            runtime?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            runtime = null;
            lifetime.Dispose();
        }
        base.Dispose(disposing);
    }
}
