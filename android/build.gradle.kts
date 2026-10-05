// Toolchain pinned to what the Dockerfile installs: Gradle 8.11.1, AGP 8.7.3, Kotlin 2.0.21, JDK 17.
plugins {
    id("com.android.application") version "8.7.3" apply false
    id("org.jetbrains.kotlin.android") version "2.0.21" apply false
    id("org.jetbrains.kotlin.jvm") version "2.0.21" apply false
}
