package com.filipkin.omtx.camera

import android.content.Context
import android.net.nsd.NsdManager
import android.net.nsd.NsdServiceInfo
import android.os.Build
import android.provider.Settings
import android.util.Log

/** DNS-SD registration as `_omtx._tcp`, instance name `DEVICE (Source)` (PROTOCOL-OMTX.md §5). */
class Discovery(private val ctx: Context) {
    private val nsd = ctx.getSystemService(NsdManager::class.java)
    private var listener: NsdManager.RegistrationListener? = null
    @Volatile var registeredName: String? = null
        private set

    fun register(sourceName: String, port: Int) {
        unregister()
        val name = instanceName(deviceName(ctx), sourceName)
        val info = NsdServiceInfo().apply {
            serviceName = name
            serviceType = SERVICE_TYPE
            setPort(port)
        }
        val l = object : NsdManager.RegistrationListener {
            override fun onServiceRegistered(si: NsdServiceInfo) {
                registeredName = si.serviceName
                Log.i(TAG, "registered ${si.serviceName} port $port")
            }
            override fun onRegistrationFailed(si: NsdServiceInfo, errorCode: Int) {
                Log.e(TAG, "registration failed $errorCode")
            }
            override fun onServiceUnregistered(si: NsdServiceInfo) { registeredName = null }
            override fun onUnregistrationFailed(si: NsdServiceInfo, errorCode: Int) {
                Log.w(TAG, "unregistration failed $errorCode")
            }
        }
        listener = l
        try {
            nsd.registerService(info, NsdManager.PROTOCOL_DNS_SD, l)
        } catch (e: Exception) {
            Log.e(TAG, "registerService", e)
            listener = null
        }
    }

    fun unregister() {
        val l = listener ?: return
        listener = null
        registeredName = null
        try { nsd.unregisterService(l) } catch (e: Exception) { Log.w(TAG, "unregisterService: $e") }
    }

    companion object {
        private const val TAG = "omtx.nsd"
        const val SERVICE_TYPE = "_omtx._tcp"

        fun deviceName(ctx: Context): String =
            Settings.Global.getString(ctx.contentResolver, Settings.Global.DEVICE_NAME)?.takeIf { it.isNotBlank() }
                ?: Build.MODEL

        /** `DEVICE (Source)` within the 63-byte DNS label limit, keeping the parentheses. */
        fun instanceName(device: String, source: String): String {
            val full = "$device ($source)"
            if (full.toByteArray(Charsets.UTF_8).size <= 63) return full
            val src = truncateUtf8(source, 30)
            val budget = 63 - 3 - src.toByteArray(Charsets.UTF_8).size
            return "${truncateUtf8(device, budget)} ($src)"
        }

        /** DNS labels are at most 63 bytes. */
        fun truncateUtf8(s: String, maxBytes: Int): String {
            if (s.toByteArray(Charsets.UTF_8).size <= maxBytes) return s
            var end = s.length
            while (end > 0 && s.substring(0, end).toByteArray(Charsets.UTF_8).size > maxBytes) end--
            return s.substring(0, end)
        }
    }
}
