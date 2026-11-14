package com.pulsedesk.mobile;

import android.content.Context;
import android.content.SharedPreferences;

/** Install-time state for the Android agent. */
final class Prefs {
    private static final String FILE = "pulsedesk";

    static SharedPreferences sp(Context c) {
        return c.getSharedPreferences(FILE, Context.MODE_PRIVATE);
    }

    static String server(Context c) {
        return sp(c).getString("server", "");
    }

    static String token(Context c) {
        return sp(c).getString("token", "");
    }

    static String deviceId(Context c) {
        return sp(c).getString("deviceId", "");
    }

    static String machine(Context c) {
        return sp(c).getString("machine", "android");
    }

    static void save(Context c, String server, String token, String deviceId, String machine) {
        sp(c).edit()
                .putString("server", server)
                .putString("token", token)
                .putString("deviceId", deviceId)
                .putString("machine", machine)
                .apply();
    }

    static void clear(Context c) {
        sp(c).edit().clear().apply();
    }

    private Prefs() { }
}
