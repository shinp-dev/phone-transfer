package com.shinpstudio.phonetransfer.ui

import android.app.Application
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import com.shinpstudio.phonetransfer.data.PairingRepository
import com.shinpstudio.phonetransfer.data.SavedPc
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.TimeoutCancellationException
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext

data class HomeState(
    val connectionLabel: String = "PC未接続",
    val busy: Boolean = false,
    val comparisonCode: String? = null,
    val pcs: List<SavedPc> = emptyList()
)

class HomeViewModel(application: Application) : AndroidViewModel(application) {
    private val repository = PairingRepository(application)
    private val mutableState = MutableStateFlow(HomeState())
    val state = mutableState.asStateFlow()
    private var operation: Job? = null

    init {
        runOperation { mutableState.value = state.value.copy(pcs = repository.saved()) }
    }

    fun pair(payload: String) = runOperation {
        mutableState.value = state.value.copy(connectionLabel = "登録要求を送信中")
        val pc = repository.pair(payload) { code ->
            withContext(Dispatchers.Main.immediate) {
                mutableState.value = state.value.copy(
                    comparisonCode = code,
                    connectionLabel = "PCの番号を確認してPC側で承認してください"
                )
            }
        }
        mutableState.value =
            state.value.copy(
                pcs = repository.saved(),
                connectionLabel = "${pc.displayName} に接続しました"
            )
    }

    fun connect(pc: SavedPc) = runOperation {
        repository.connect(pc)
        mutableState.value = state.value.copy(connectionLabel = "${pc.displayName} に接続しました")
    }

    fun forget(pc: SavedPc) = runOperation {
        repository.forget(pc.deviceId)
        mutableState.value =
            state.value.copy(
                pcs = repository.saved(),
                connectionLabel = "スマホの登録情報を削除しました。再登録前にPC側でも端末を解除してください。"
            )
    }

    fun cancel() {
        operation?.cancel()
        mutableState.value = state.value.copy(
            comparisonCode = null,
            connectionLabel = "中止しました。PC側で承認済みの場合はPCの端末一覧から解除してください。"
        )
    }

    private fun runOperation(block: suspend () -> Unit) {
        if (operation?.isCompleted == false) return
        operation = viewModelScope.launch {
            mutableState.value = state.value.copy(busy = true)
            try {
                block()
            } catch (error: TimeoutCancellationException) {
                mutableState.value =
                    state.value.copy(connectionLabel = "登録の有効期限が切れました。PCで新しいQRを表示してください。")
            } catch (error: CancellationException) {
                throw error
            } catch (error: Exception) {
                mutableState.value =
                    state.value.copy(
                        connectionLabel = "接続または保存に失敗しました。LANとQR期限を確認してください。" +
                            "再登録する場合はPC側の登録を解除してください。"
                    )
            } finally {
                mutableState.value = state.value.copy(busy = false, comparisonCode = null)
            }
        }
    }
}
