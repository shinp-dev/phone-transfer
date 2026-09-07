package com.shinpstudio.phonetransfer.ui

import androidx.lifecycle.ViewModel
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow

data class HomeState(val connectionLabel: String = "PC未接続", val ready: Boolean = false)

class HomeViewModel : ViewModel() {
    private val mutableState = MutableStateFlow(HomeState())
    val state: StateFlow<HomeState> = mutableState.asStateFlow()
}
