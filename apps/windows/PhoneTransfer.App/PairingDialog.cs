using System.Data.Common;
using System.Diagnostics;
using System.Text.Json;
using PhoneTransfer.Host;
using QRCoder;

namespace PhoneTransfer.App;

internal sealed class PairingDialog : Form
{
    private readonly WindowsServerRuntime runtime;
    private readonly PictureBox qr = new() { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.White };
    private readonly Label prompt = new() { AutoSize = true, MaximumSize = new Size(420, 0) };
    private readonly Button approve = new() { Text = "番号が一致・登録する", AutoSize = true, Enabled = false };
    private readonly Button deny = new() { Text = "登録しない", AutoSize = true, Enabled = false };
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 500 };
    private readonly Stopwatch lifetime = Stopwatch.StartNew();
    private Guid? requestId;

    public PairingDialog(WindowsServerRuntime runtime)
    {
        this.runtime = runtime;
        Text = "スマホを登録 — Phone Transfer";
        ClientSize = new Size(460, 620);
        MinimumSize = new Size(440, 600);
        StartPosition = FormStartPosition.CenterParent;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(20), RowCount = 4, ColumnCount = 1 };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(new Label { Text = "同じWi-FiのスマホでQRコードを読み取ってください。", AutoSize = true }, 0, 0);
        layout.Controls.Add(qr, 0, 1);
        layout.Controls.Add(prompt, 0, 2);
        var actions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        actions.Controls.AddRange([approve, deny]);
        layout.Controls.Add(actions, 0, 3);
        Controls.Add(layout);
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(JsonSerializer.Serialize(runtime.NewQr()), QRCodeGenerator.ECCLevel.M);
        using var renderer = new PngByteQRCode(data);
        using var stream = new MemoryStream(renderer.GetGraphic(4));
        using var image = Image.FromStream(stream);
        qr.Image = new Bitmap(image);
        approve.Click += (_, _) => Approve();
        deny.Click += (_, _) =>
        {
            if (requestId is Guid id) runtime.Pairing.Deny(id);
            Finish("登録を取り消しました。必要なら画面を閉じてやり直してください。");
        };
        timer.Tick += (_, _) => RefreshRequest();
        FormClosed += (_, _) => runtime.Pairing.EndChallenge();
        RefreshRequest();
        timer.Start();
    }

    private void RefreshRequest()
    {
        var candidate = runtime.Pairing.PendingApproval();
        if (candidate is not null)
        {
            requestId = candidate.RequestId;
            prompt.Text = $"{candidate.DisplayName} から登録要求が届きました。\nスマホにも次の番号が表示されていますか？\n\n{candidate.ComparisonCode}";
            approve.Enabled = true;
            deny.Enabled = true;
            return;
        }
        if (requestId is not null || lifetime.Elapsed >= TimeSpan.FromSeconds(120))
        {
            Finish("有効期限が切れました。画面を閉じてQRコードを再表示してください。");
            return;
        }
        prompt.Text = $"QRコードの残り時間: {Math.Max(0, 120 - (int)lifetime.Elapsed.TotalSeconds)} 秒";
    }

    private void Approve()
    {
        if (requestId is not Guid id) return;
        try
        {
            Finish(runtime.Pairing.Approve(id)
                ? "登録しました。スマホ側で接続完了を確認してください。"
                : "登録できませんでした。期限切れ、または既に登録されている端末です。");
        }
        catch (Exception exception) when (exception is IOException or DbException or UnauthorizedAccessException)
        {
            prompt.Text = "登録情報を保存できませんでした。PCの空き容量とアクセス権を確認し、もう一度お試しください。";
        }
    }

    private void Finish(string message)
    {
        timer.Stop();
        approve.Enabled = false;
        deny.Enabled = false;
        prompt.Text = message;
        qr.Image?.Dispose();
        qr.Image = null;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            timer.Dispose();
            qr.Image?.Dispose();
        }
        base.Dispose(disposing);
    }
}
