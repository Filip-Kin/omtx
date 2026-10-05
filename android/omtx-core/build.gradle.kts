import org.jetbrains.kotlin.gradle.dsl.JvmTarget

// Pure Kotlin/JVM: wire format, metadata, sender state machine, bitrate control, Annex B.
// Shared by the Android app and the file-sender test tool.
plugins {
    id("org.jetbrains.kotlin.jvm")
}

java {
    sourceCompatibility = JavaVersion.VERSION_17
    targetCompatibility = JavaVersion.VERSION_17
}

kotlin {
    compilerOptions { jvmTarget.set(JvmTarget.JVM_17) }
}

dependencies {
    testImplementation(kotlin("test"))
}

tasks.test {
    useJUnitPlatform()
    testLogging {
        events("passed", "failed", "skipped")
        exceptionFormat = org.gradle.api.tasks.testing.logging.TestExceptionFormat.FULL
    }
}

// java -jar omtx-file-sender.jar <file.h264> <fps> <width> <height> [port]
val fileSenderJar by tasks.registering(Jar::class) {
    archiveFileName.set("omtx-file-sender.jar")
    destinationDirectory.set(layout.buildDirectory.dir("dist"))
    manifest { attributes["Main-Class"] = "com.filipkin.omtx.core.FileSender" }
    duplicatesStrategy = DuplicatesStrategy.EXCLUDE
    from(sourceSets.main.get().output)
    dependsOn(configurations.runtimeClasspath)
    from({ configurations.runtimeClasspath.get().filter { it.name.endsWith(".jar") }.map { zipTree(it) } })
    exclude("META-INF/*.SF", "META-INF/*.DSA", "META-INF/*.RSA", "META-INF/versions/**", "module-info.class")
}
