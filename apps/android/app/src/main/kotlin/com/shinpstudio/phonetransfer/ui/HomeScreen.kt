package com.shinpstudio.phonetransfer.ui

import androidx.activity.compose.rememberLauncherForActivityResult
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

@Composable
fun HomeScreen(viewModel: HomeViewModel = viewModel()) {
    val state by viewModel.state.collectAsStateWithLifecycle()
    var payload by remember { mutableStateOf("") }
    val scanner = rememberLauncherForActivityResult(ScanContract()) { result ->
        result.contents?.let { viewModel.pair(it) }
    }
    Column(
        modifier = Modifier.fillMaxSize().verticalScroll(rememberScrollState()).padding(24.dp),
        verticalArrangement = Arrangement.spacedBy(12.dp)
    ) {
        Text("Phone Transfer", style = MaterialTheme.typography.headlineMedium)
        Text(state.connectionLabel)
        state.comparisonCode?.let { Text(it, style = MaterialTheme.typography.displayMedium) }
        Button(enabled = !state.busy, onClick = {
            scanner.launch(ScanOptions().setDesiredBarcodeFormats(ScanOptions.QR_CODE)
                .setPrompt("PCの「スマホを登録」で表示したQRを読み取ってください")
                .setBeepEnabled(false).setBarcodeImageEnabled(false))
        }) { Text("PCのQRを読み取る") }
        OutlinedTextField(value = payload, onValueChange = { if (it.length <= 4096) payload = it },
            enabled = !state.busy, label = { Text("QRの内容を貼り付け（カメラが使えない場合）") })
        Button(enabled = !state.busy && payload.isNotBlank(), onClick = {
            val input = payload
            payload = ""
            viewModel.pair(input)
        }) { Text("登録する") }
        if (state.busy) TextButton(onClick = viewModel::cancel) { Text("中止") }
        state.pcs.forEach { pc ->
            Text(pc.displayName)
            Button(enabled = !state.busy, onClick = { viewModel.connect(pc) }) { Text("接続を確認") }
            TextButton(enabled = !state.busy, onClick = { viewModel.forget(pc) }) { Text("このスマホから登録を削除") }
        }
        Text("ファイル・テキスト転送は準備中です")
        Text("App ${BuildConfig.VERSION_NAME} / Build ${BuildConfig.VERSION_CODE} / Protocol 1")
    }
}
