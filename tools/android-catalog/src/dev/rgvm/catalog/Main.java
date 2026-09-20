package dev.rgvm.catalog;

import android.app.ActivityManager;
import android.content.Context;
import android.content.pm.ApplicationInfo;
import android.content.pm.PackageInfo;
import android.content.pm.PackageManager;
import android.graphics.Bitmap;
import android.graphics.Canvas;
import android.graphics.drawable.Drawable;
import android.os.Looper;
import android.os.UserHandle;
import android.util.Base64;
import java.io.ByteArrayOutputStream;
import java.util.ArrayList;
import java.util.Collections;
import java.util.Comparator;
import java.util.HashMap;
import java.util.List;
import java.util.Map;
import org.json.JSONArray;
import org.json.JSONObject;

/** Read-only package metadata bridge. No package installation, data mutation or network. */
public final class Main {
    private static Context context(int userId) throws Exception {
        Looper.prepareMainLooper();
        Class<?> activityThread = Class.forName("android.app.ActivityThread");
        Object thread = activityThread.getMethod("systemMain").invoke(null);
        Context system = (Context) activityThread.getMethod("getSystemContext").invoke(thread);
        UserHandle user = (UserHandle) UserHandle.class.getMethod("of", int.class).invoke(null, userId);
        return (Context) Context.class.getMethod("createContextAsUser", UserHandle.class, int.class)
            .invoke(system, user, 0);
    }

    private static String icon(ApplicationInfo app, PackageManager pm) throws Exception {
        Drawable drawable = app.loadIcon(pm);
        if (drawable == null) return null;
        Bitmap bitmap = Bitmap.createBitmap(48, 48, Bitmap.Config.ARGB_8888);
        try {
            drawable.setBounds(0, 0, 48, 48);
            drawable.draw(new Canvas(bitmap));
            ByteArrayOutputStream output = new ByteArrayOutputStream();
            if (!bitmap.compress(Bitmap.CompressFormat.PNG, 100, output)) return null;
            return Base64.encodeToString(output.toByteArray(), Base64.NO_WRAP);
        } finally { bitmap.recycle(); }
    }

    public static void main(String[] args) {
        try {
            if (args.length != 3) throw new IllegalArgumentException("userId includeSystem includeIcons required");
            int userId = Integer.parseInt(args[0]);
            if (userId < 0) throw new IllegalArgumentException("userId must be nonnegative");
            boolean includeSystem = Boolean.parseBoolean(args[1]);
            boolean includeIcons = Boolean.parseBoolean(args[2]);
            Context context = context(userId);
            PackageManager pm = context.getPackageManager();
            Map<String, List<Integer>> running = new HashMap<String, List<Integer>>();
            String processError = null;
            try {
                List<ActivityManager.RunningAppProcessInfo> processes =
                    ((ActivityManager) context.getSystemService(Context.ACTIVITY_SERVICE)).getRunningAppProcesses();
                if (processes == null) processError = "process_list_unavailable";
                else for (ActivityManager.RunningAppProcessInfo process : processes) {
                    if (process.pkgList == null) continue;
                    for (String member : process.pkgList) {
                        String key = process.uid + ":" + member;
                        if (!running.containsKey(key)) running.put(key, new ArrayList<Integer>());
                        running.get(key).add(process.pid);
                    }
                }
            } catch (Exception error) { processError = error.getClass().getSimpleName(); }
            List<PackageInfo> packages = pm.getInstalledPackages(0);
            Collections.sort(packages, new Comparator<PackageInfo>() {
                public int compare(PackageInfo a, PackageInfo b) { return a.packageName.compareTo(b.packageName); }
            });
            JSONArray entries = new JSONArray();
            for (PackageInfo pkg : packages) {
                ApplicationInfo app = pkg.applicationInfo;
                if (app == null) continue;
                boolean system = (app.flags & ApplicationInfo.FLAG_SYSTEM) != 0;
                if (system && !includeSystem) continue;
                JSONObject row = new JSONObject();
                row.put("package", pkg.packageName);
                row.put("userId", userId);
                row.put("uid", app.uid);
                row.put("system", system);
                row.put("enabled", app.enabled);
                row.put("versionName", pkg.versionName == null ? "" : pkg.versionName);
                row.put("versionCode", pkg.getLongVersionCode());
                row.put("firstInstallTime", pkg.firstInstallTime);
                row.put("lastUpdateTime", pkg.lastUpdateTime);
                row.put("sourceDir", app.sourceDir);
                row.put("privateDirectory", app.dataDir);
                try { row.put("credentialProtectedDirectory", ApplicationInfo.class.getField("credentialProtectedDataDir").get(app)); }
                catch (ReflectiveOperationException unavailable) { row.put("credentialDirectoryError", unavailable.getClass().getSimpleName()); }
                row.put("deviceProtectedDirectory", app.deviceProtectedDataDir);
                row.put("processName", app.processName);
                String processKey = app.uid + ":" + pkg.packageName;
                row.put("runningPids", new JSONArray(running.containsKey(processKey) ? running.get(processKey) : Collections.emptyList()));
                if (processError != null) row.put("runningStateError", processError);
                String label = pkg.packageName;
                try {
                    CharSequence value = app.loadLabel(pm);
                    if (value != null && value.toString().trim().length() > 0) label = value.toString();
                } catch (Exception error) { row.put("labelError", error.getClass().getSimpleName()); }
                row.put("name", label);
                row.put("nameSource", label.equals(pkg.packageName) ? "package_fallback" : "package_manager");
                if (includeIcons) {
                    try {
                        String png = icon(app, pm);
                        if (png != null) row.put("iconPng", png);
                    } catch (Exception error) { row.put("iconError", error.getClass().getSimpleName()); }
                }
                entries.put(row);
            }
            JSONObject result = new JSONObject();
            result.put("schemaVersion", 1);
            result.put("userId", userId);
            result.put("locale", context.getResources().getConfiguration().getLocales().toLanguageTags());
            result.put("observedAtUnixMs", System.currentTimeMillis());
            result.put("entries", entries);
            System.out.println(result.toString());
            System.exit(0);
        } catch (Throwable error) {
            error.printStackTrace(System.err);
            System.exit(1);
        }
    }
}
