plugins {
    id("com.android.application") version "8.13.2" apply false
    kotlin("android") version "2.2.21" apply false
    kotlin("jvm") version "2.2.21" apply false
    kotlin("plugin.serialization") version "2.2.21" apply false
    id("org.jetbrains.kotlin.plugin.compose") version "2.2.21" apply false
    id("com.diffplug.spotless") version "7.0.2"
}
spotless {
    kotlin {
        target("**/src/**/*.kt", "../../packages/protocol/kotlin/src/**/*.kt")
        targetExclude("**/build/**")
        ktlint("1.5.0")
    }
    kotlinGradle {
        target("*.gradle.kts", "app/*.gradle.kts", "../../packages/protocol/kotlin/*.gradle.kts")
        ktlint("1.5.0")
    }
}
