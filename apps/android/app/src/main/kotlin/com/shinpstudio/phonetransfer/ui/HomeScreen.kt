package com.shinpstudio.phonetransfer.ui

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.padding
import androidx.compose.material3.Button
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.lifecycle.viewmodel.compose.viewModel
import com.shinpstudio.phonetransfer.BuildConfig

@Composable
fun HomeScreen(viewModel: HomeViewModel = viewModel()) {
    val state by viewModel.state.collectAsStateWithLifecycle()
    Column(
        modifier = Modifier.fillMaxSize().padding(24.dp),
        verticalArrangement = Arrangement.spacedBy(12.dp)
    ) {
        Text("Phone Transfer")
        Text(state.connectionLabel)
        Text("開発中: ペアリング機能は準備中です")
        listOf("ファイルを送る", "PCファイルを見る", "テキストを送る", "履歴").forEach { label ->
            Button(onClick = {}, enabled = state.ready) { Text(label) }
        }
        Text("App ${BuildConfig.VERSION_NAME} / Build ${BuildConfig.VERSION_CODE} / Protocol 1")
    }
}
