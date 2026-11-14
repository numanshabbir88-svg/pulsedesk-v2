package com.pulsedesk.mobile;

import java.io.ByteArrayOutputStream;
import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;
import java.net.HttpURLConnection;
import java.net.URL;
import java.nio.charset.StandardCharsets;

/** Tiny HTTP helper (no third-party libraries, keeps the APK small). */
final class Api {
    private static final int TIMEOUT = 15000;

    static String get(String url, String token) throws IOException {
        HttpURLConnection c = open(url, "GET", token);
        try { return read(c); } finally { c.disconnect(); }
    }

    static String postJson(String url, String token, String json) throws IOException {
        HttpURLConnection c = open(url, "POST", token);
        c.setRequestProperty("Content-Type", "application/json; charset=utf-8");
        byte[] body = json.getBytes(StandardCharsets.UTF_8);
        c.setDoOutput(true);
        c.setFixedLengthStreamingMode(body.length);
        try {
            OutputStream os = c.getOutputStream();
            os.write(body);
            os.close();
            return read(c);
        } finally {
            c.disconnect();
        }
    }

    static String postBytes(String url, String token, String contentType, byte[] data) throws IOException {
        HttpURLConnection c = open(url, "POST", token);
        c.setRequestProperty("Content-Type", contentType);
        c.setDoOutput(true);
        c.setFixedLengthStreamingMode(data.length);
        try {
            OutputStream os = c.getOutputStream();
            os.write(data);
            os.close();
            return read(c);
        } finally {
            c.disconnect();
        }
    }

    static String postMultipart(String url, String token, byte[] jpeg, String filename) throws IOException {
        String boundary = "----PulseDeskBoundary" + System.nanoTime();
        ByteArrayOutputStream buf = new ByteArrayOutputStream(jpeg.length + 512);
        write(buf, "--" + boundary + "\r\n");
        write(buf, "Content-Disposition: form-data; name=\"file\"; filename=\"" + filename + "\"\r\n");
        write(buf, "Content-Type: image/jpeg\r\n\r\n");
        buf.write(jpeg);
        write(buf, "\r\n--" + boundary + "--\r\n");

        HttpURLConnection c = open(url, "POST", token);
        c.setRequestProperty("Content-Type", "multipart/form-data; boundary=" + boundary);
        byte[] body = buf.toByteArray();
        c.setDoOutput(true);
        c.setFixedLengthStreamingMode(body.length);
        try {
            OutputStream os = c.getOutputStream();
            os.write(body);
            os.close();
            return read(c);
        } finally {
            c.disconnect();
        }
    }

    private static void write(ByteArrayOutputStream b, String s) {
        byte[] bytes = s.getBytes(StandardCharsets.UTF_8);
        b.write(bytes, 0, bytes.length);
    }

    private static HttpURLConnection open(String url, String method, String token) throws IOException {
        HttpURLConnection c = (HttpURLConnection) new URL(url).openConnection();
        c.setRequestMethod(method);
        c.setConnectTimeout(TIMEOUT);
        c.setReadTimeout(TIMEOUT);
        c.setRequestProperty("User-Agent", "PulseDesk-Android/2.0");
        if (token != null && !token.isEmpty()) c.setRequestProperty("X-Device-Token", token);
        return c;
    }

    private static String read(HttpURLConnection c) throws IOException {
        int code = c.getResponseCode();
        InputStream is = code >= 400 ? c.getErrorStream() : c.getInputStream();
        String text = "";
        if (is != null) {
            ByteArrayOutputStream out = new ByteArrayOutputStream();
            byte[] chunk = new byte[8192];
            int n;
            while ((n = is.read(chunk)) > 0) out.write(chunk, 0, n);
            is.close();
            text = out.toString("UTF-8");
        }
        if (code >= 400) throw new IOException("HTTP " + code + ": " + text);
        return text;
    }

    private Api() { }
}
