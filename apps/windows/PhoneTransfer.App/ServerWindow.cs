using System.Data.Common;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
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
    private readonly Label shareStatus = new() { AutoSize = true, MaximumSize = new Size(560, 0) };
    private readonly Button chooseShare = new() { Text = "受信フォルダを選択", AutoSize = true };
    private readonly Button clearShare = new() { Text = "受信フォルダ設定を解除", AutoSize = true, Enabled = false };
    private bool closing;
    private bool starting;
    private bool resourcesDisposed;

    public ServerWindow()
    {
        dataDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PhoneTransfer");
        shareConfiguration = new WindowsShareConfiguration(dataDirectory);

        Text = "Phone Transfer";
        ClientSize = new Size(620, 440);
        MinimumSize = new Size(520, 400);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(20), RowCount = 8, ColumnCount = 1 };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
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
        layout.Controls.Add(new Label
        {
            AutoSize = true,
            Text = "ファイル・テキスト転送APIは開発中です。\n閉じるとトレイで待機します。 App 0.1.0 / Build 1 / Protocol 1"
        }, 0, 7);
        Controls.Add(layout);
        Shown += async (_, _) =>
        {
            RefreshShareConfiguration();
            await StartAsync();
        };
        reconnect.Click += async (_, _) => await StartAsync();
        chooseShare.Click += (_, _) => ChooseShare();
        clearShare.Click += (_, _) => ClearShare();
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
            Description = "スマホから受信するフォルダを選択してください。ファイルAPIは安全なファイルI/O実装後に有効化します。",
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
            status.Text = "受信フォルダ設定を保存しました。ファイル転送APIはまだ無効です。";
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
            status.Text = "受信フォルダ設定を解除しました。";
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
                ? "受信フォルダ: 未設定（ファイル転送APIはまだ無効です）"
                : $"受信フォルダ: {root}\n（設定済みですが、ファイル転送APIはまだ無効です）";
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
            var started = await WindowsServerRuntime.StartAsync(dataDirectory, adapter.Address, lifetime.Token);
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
