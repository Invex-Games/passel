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

    flavorDimensions += "environment"

    productFlavors {
        create("development") {
            dimension = "environment"
            applicationIdSuffix = ".dev"
            resValue("string", "app_name", "Passel Dev")
        }
        create("production") {
            dimension = "environment"
            resValue("string", "app_name", "Passel")
        }
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
                        "prod release builds WILL fail. dev release will use debug keys."
            )
        }
    }

    buildTypes {
        release {
            signingConfig = if (hasReleaseSigningConfig) {
                signingConfigs.getByName("release")
            } else {
                // Fall back to debug keys only for the dev flavour;
                // prod release without a keystore will still fail at upload time.
                signingConfigs.getByName("debug")
            }
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

