import java.util.Properties
import org.jetbrains.kotlin.gradle.dsl.JvmTarget
import org.jetbrains.kotlin.gradle.tasks.KotlinCompile

plugins {
    id("com.android.application")
    id("kotlin-android")
    // The Flutter Gradle Plugin must be applied after the Android and Kotlin Gradle plugins.
    id("dev.flutter.flutter-gradle-plugin")
}

val keystoreProperties = Properties()

if (System.getenv("SIGNING_RELEASE_STORE_PASSWORD") != null)
    keystoreProperties.setProperty("storePassword", System.getenv("SIGNING_RELEASE_STORE_PASSWORD"))

if (System.getenv("SIGNING_RELEASE_KEY_PASSWORD") != null)
    keystoreProperties.setProperty("keyPassword", System.getenv("SIGNING_RELEASE_KEY_PASSWORD"))

if (System.getenv("SIGNING_RELEASE_KEY_ALIAS") != null)
    keystoreProperties.setProperty("keyAlias", System.getenv("SIGNING_RELEASE_KEY_ALIAS"))

if (System.getenv("SIGNING_RELEASE_STORE_FILE") != null)
    keystoreProperties.setProperty("storeFile", System.getenv("SIGNING_RELEASE_STORE_FILE"))

fun signingProp(key: String): String? =
    keystoreProperties.getProperty(key)?.takeIf { it.isNotBlank() }
        ?: System.getenv("SIGNING_${key.uppercase()}")

val hasReleaseSigningConfig = listOf("storePassword", "keyPassword", "keyAlias", "storeFile")
    .all { signingProp(it) != null }

android {
    namespace = "com.invex.passel.mobileapp"
    compileSdk = flutter.compileSdkVersion
    ndkVersion = flutter.ndkVersion

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }

    defaultConfig {
        applicationId = "com.invex.passel.mobileapp"
        minSdk = flutter.minSdkVersion
        targetSdk = flutter.targetSdkVersion
        versionCode = flutter.versionCode
        versionName = flutter.versionName
    }

    signingConfigs {
        if (hasReleaseSigningConfig) {
            create("release") {
                keyAlias = signingProp("keyAlias")
                keyPassword = signingProp("keyPassword")
                storeFile = signingProp("storeFile")?.let { file(it) }
                storePassword = signingProp("storePassword")
            }
        } else {
            logger.warn(
                "[signing] key properties not found / incomplete – " +
                        "productionRelease builds WILL fail. developmentRelease will use debug keys."
            )
        }
    }

    flavorDimensions += "environment"

    // Release signing is chosen per flavor (the release build type sets no
    // signingConfig, so the flavor's applies; debug builds keep debug keys).
    productFlavors {
        create("development") {
            dimension = "environment"
            applicationIdSuffix = ".dev"
            resValue("string", "app_name", "Passel Dev")
            // Dev builds may fall back to debug keys when no keystore is set.
            signingConfig = if (hasReleaseSigningConfig) {
                signingConfigs.getByName("release")
            } else {
                signingConfigs.getByName("debug")
            }
        }
        create("production") {
            dimension = "environment"
            resValue("string", "app_name", "Passel")
            // Never debug-signed: without a keystore productionRelease fails
            // (see the preProductionReleaseBuild check below).
            signingConfig = if (hasReleaseSigningConfig) {
                signingConfigs.getByName("release")
            } else {
                null
            }
        }
    }

    buildTypes {
        release {
            // Flutter's Gradle plugin adds its own keep rules for the engine
            // and plugins on top of the default optimized ProGuard config.
            isMinifyEnabled = true
            isShrinkResources = true
            proguardFiles(getDefaultProguardFile("proguard-android-optimize.txt"))
        }
    }
}

// Fail fast with a clear message instead of producing an unsigned store build.
tasks.matching { it.name == "preProductionReleaseBuild" }.configureEach {
    val releaseSigningAvailable = hasReleaseSigningConfig
    doFirst {
        if (!releaseSigningAvailable) {
            throw GradleException(
                "productionRelease requires release signing. Set " +
                        "SIGNING_RELEASE_STORE_FILE, SIGNING_RELEASE_STORE_PASSWORD, " +
                        "SIGNING_RELEASE_KEY_ALIAS and SIGNING_RELEASE_KEY_PASSWORD."
            )
        }
    }
}

flutter {
    source = "../.."
}

tasks.withType<KotlinCompile> {
    compilerOptions {
        jvmTarget.set(JvmTarget.JVM_17)
    }
}

