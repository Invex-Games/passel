package com.invex.passel.mobileapp

import android.content.Intent
import io.flutter.embedding.android.FlutterActivity
import io.flutter.embedding.engine.FlutterEngine
import io.flutter.plugin.common.MethodChannel

/**
 * Hands text shared into the app to the Dart side over the `passel/share` channel: the share sheet's
 * `ACTION_SEND` with `text/plain`, and the text selection menu's `ACTION_PROCESS_TEXT`. Text that
 * arrives before Dart is ready waits until Dart asks for it; text that arrives while the app runs is
 * pushed at once. No plugin: this is all the platform side there is.
 */
class MainActivity : FlutterActivity() {
    private var channel: MethodChannel? = null
    private var pendingText: String? = null

    override fun configureFlutterEngine(flutterEngine: FlutterEngine) {
        super.configureFlutterEngine(flutterEngine)
        pendingText = takeSharedText(intent)
        channel = MethodChannel(flutterEngine.dartExecutor.binaryMessenger, CHANNEL).also { channel ->
            channel.setMethodCallHandler { call, result ->
                when (call.method) {
                    "takePendingText" -> {
                        result.success(pendingText)
                        pendingText = null
                    }
                    else -> result.notImplemented()
                }
            }
        }
    }

    override fun onNewIntent(intent: Intent) {
        super.onNewIntent(intent)
        setIntent(intent)
        val text = takeSharedText(intent) ?: return
        val channel = channel
        if (channel == null) pendingText = text else channel.invokeMethod("textShared", text)
    }

    override fun onDestroy() {
        channel?.setMethodCallHandler(null)
        channel = null
        super.onDestroy()
    }

    /**
     * The shared text of [intent], trimmed, or null when it carries none. The extras are removed so the
     * same text is not offered again when the activity is recreated, for example on rotation.
     */
    private fun takeSharedText(intent: Intent?): String? {
        if (intent == null) return null
        val text = when (intent.action) {
            Intent.ACTION_SEND ->
                if (intent.type == "text/plain") intent.getStringExtra(Intent.EXTRA_TEXT) else null
            Intent.ACTION_PROCESS_TEXT -> intent.getCharSequenceExtra(Intent.EXTRA_PROCESS_TEXT)?.toString()
            else -> null
        }
        intent.removeExtra(Intent.EXTRA_TEXT)
        intent.removeExtra(Intent.EXTRA_PROCESS_TEXT)
        return text?.trim()?.takeIf { it.isNotEmpty() }
    }

    private companion object {
        const val CHANNEL = "passel/share"
    }
}
