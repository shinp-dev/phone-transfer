package com.shinpstudio.phonetransfer.ui

import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.Button
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.lifecycle.viewmodel.compose.viewModel
import com.journeyapps.barcodescanner.ScanContract
import com.journeyapps.barcodescanner.ScanOptions
import com.shinpstudio.phonetransfer.BuildConfig
import com.shinpstudio.phonetransfer.protocol.FileEntry
import com.shinpstudio.phonetransfer.transfer.TransferKind
import com.shinpstudio.phonetransfer.transfer.TransferServiceState

@Composable
fun HomeScreen(viewModel: HomeViewModel = viewModel()) {
    val state by viewModel.state.collectAsStateWithLifecycle()
    var payload by remember { mutableStateOf("") }
    var pendingDownload by remember { mutableStateOf<FileEntry?>(null) }
    val scanner =
        rememberLauncherForActivityResult(ScanContract()) { result ->
            result.contents?.let { viewModel.pair(it) }
        }
    val uploadPicker =
        rememberLauncherForActivityResult(ActivityResultContracts.OpenDocument()) { uri ->
            uri?.let(viewModel::upload)
        }
    val downloadPicker =
        rememberLauncherForActivityResult(
            ActivityResultContracts.CreateDocument("application/octet-stream")
        ) { uri ->
            val entry = pendingDownload
            pendingDownload = null
            if (uri != null && entry != null) viewModel.download(entry, uri)
        }
    val transfer = state.transfer
    val transferBlocking =
        transfer is TransferServiceState.Running ||
            transfer is TransferServiceState.Resumable ||
            transfer is TransferServiceState.RecoveryBlocked
    val interactive = !state.busy && !transferBlocking

    Column(
        modifier =
            Modifier
                .fillMaxSize()
                .verticalScroll(rememberScrollState())
                .padding(24.dp),
        verticalArrangement = Arrangement.spacedBy(12.dp)
    ) {
        Text("Phone Transfer", style = MaterialTheme.typography.headlineMedium)
        Text(state.connectionLabel)
        state.comparisonCode?.let {
            Text(it, style = MaterialTheme.typography.displayMedium)
        }
        Button(
            enabled = interactive,
            onClick = {
                scanner.launch(
                    ScanOptions()
                        .setDesiredBarcodeFormats(ScanOptions.QR_CODE)
                        .setPrompt("PCの「スマホを登録」で表示したQRを読み取ってください")
                        .setBeepEnabled(false)
                        .setBarcodeImageEnabled(false)
                )
            }
        ) {
            Text("PCのQRを読み取る")
        }
        OutlinedTextField(
            value = payload,
            onValueChange = { if (it.length <= 4096) payload = it },
            enabled = interactive,
            label = { Text("QRの内容を貼り付け（カメラが使えない場合）") }
        )
        Button(
            enabled = interactive && payload.isNotBlank(),
            onClick = {
                val input = payload
                payload = ""
                viewModel.pair(input)
            }
        ) {
            Text("登録する")
        }
        if (state.busy) {
            TextButton(onClick = viewModel::cancel) {
                Text("中止")
            }
        }

        state.pcs.forEach { pc ->
            Text(pc.displayName)
            Button(
                enabled = interactive,
                onClick = { viewModel.connect(pc) }
            ) {
                Text("接続してファイルを見る")
            }
            TextButton(
                enabled = interactive,
                onClick = { viewModel.forget(pc) }
            ) {
                Text("このスマホから登録を削除")
            }
        }

        state.share?.let { share ->
            Text("PCの共有フォルダ", style = MaterialTheme.typography.titleMedium)
            Text(if (state.currentPath.isEmpty()) "/" else "/${state.currentPath}")
            Button(
                enabled = interactive,
                onClick = viewModel::refreshFiles
            ) {
                Text("一覧を更新")
            }
            if (state.currentPath.isNotEmpty()) {
                TextButton(
                    enabled = interactive,
                    onClick = viewModel::goUp
                ) {
                    Text("1つ上のフォルダへ")
                }
            }
            Button(
                enabled = interactive && share.writable,
                onClick = { uploadPicker.launch(arrayOf("*/*")) }
            ) {
                Text("このフォルダへファイルを送る")
            }
            if (!share.writable) {
                Text("この端末にはアップロード権限がありません")
            }

            state.entries.forEach { entry ->
                val label =
                    if (entry.kind == "directory") {
                        "📁 ${entry.name}"
                    } else {
                        "📄 ${entry.name} (${entry.size} bytes)"
                    }
                Text(label)
                if (entry.kind == "directory") {
                    TextButton(
                        enabled = interactive,
                        onClick = { viewModel.openDirectory(entry) }
                    ) {
                        Text("開く")
                    }
                } else {
                    TextButton(
                        enabled = interactive,
                        onClick = {
                            pendingDownload = entry
                            downloadPicker.launch(entry.name)
                        }
                    ) {
                        Text("スマホに保存")
                    }
                }
            }
        }

        when (transfer) {
            is TransferServiceState.Running -> {
                val action = if (transfer.kind == TransferKind.Upload) "送信" else "受信"
                val progress =
                    if (transfer.totalBytes > 0) {
                        val percent =
                            (
                                transfer.transferredBytes.coerceAtMost(transfer.totalBytes) * 100 /
                                    transfer.totalBytes
                                )
                        "${action}中: $percent% (${transfer.transferredBytes} / ${transfer.totalBytes} bytes)"
                    } else {
                        "${action}を準備中"
                    }
                Text(progress)
                Text("転送はForeground Serviceが所有します。画面を切り替えても処理を継続します。")
                TextButton(onClick = viewModel::cancel) {
                    Text("ファイル転送を中止")
                }
            }

            is TransferServiceState.Resumable -> {
                val action = if (transfer.kind == TransferKind.Upload) "送信" else "受信"
                Text("中断した${action}があります (${transfer.reason})")
                if (transfer.totalBytes > 0) {
                    Text("前回確認済み: ${transfer.transferredBytes} / ${transfer.totalBytes} bytes")
                }
                if (transfer.canResume) {
                    Text("再開前にPCの本人性、SAF権限、転送状態を再確認します。送信は元ファイルを再ハッシュします。")
                    Button(onClick = viewModel::resumeTransfer) {
                        Text("安全確認して転送を再開")
                    }
                } else {
                    Text("この転送は安全に再開できません。中止処理だけを行います。")
                }
                TextButton(onClick = viewModel::cancel) {
                    Text("この転送を中止")
                }
            }

            is TransferServiceState.RecoveryBlocked -> {
                Text("転送復旧データを検証できないため、新しい転送を開始しません。")
                Text("アプリの復旧データを確認するまで安全側に停止します: ${transfer.code}")
            }

            is TransferServiceState.Completed -> {
                Text("直前のファイル転送は完了しました")
            }

            is TransferServiceState.Failed -> {
                Text("直前のファイル転送は失敗しました: ${transfer.code}")
            }

            is TransferServiceState.Cancelled -> {
                Text("直前のファイル転送は中止されました")
            }

            TransferServiceState.Idle -> Unit
        }

        Text("送信はAndroid再起動後も安全確認して再開できます。受信の復旧は安全側に先頭から再試行します。")
        Text("App ${BuildConfig.VERSION_NAME} / Build ${BuildConfig.VERSION_CODE} / Protocol 1")
    }
}
