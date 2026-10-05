plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.android")
}

android {
    namespace = "com.filipkin.omtx.camera"
    compileSdk = 35

    defaultConfig {
        applicationId = "com.filipkin.omtx.camera"
        minSdk = 29
        targetSdk = 35
        versionCode = 1
        versionName = "0.1.0"
    }

    buildTypes {
        // Sideload build: release optimisations off the table (no minify), signed with the
        // debug key that the Docker build keeps in a cache mount so updates install over
        // each other.
        release {
            isMinifyEnabled = false
            signingConfig = signingConfigs.getByName("debug")
        }
    }

    buildFeatures { buildConfig = true }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }
    kotlinOptions { jvmTarget = "17" }

    lint {
        checkReleaseBuilds = false
        abortOnError = false
    }
}

dependencies {
    implementation(project(":omtx-core"))
}
