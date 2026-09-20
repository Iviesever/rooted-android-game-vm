package dev.rgvm.catalog;

import android.system.Os;
import android.system.OsConstants;
import android.system.ErrnoException;
import android.system.StructStat;
import android.util.Base64;
import java.io.ByteArrayOutputStream;
import java.io.File;
import java.io.FileDescriptor;
import java.io.FileInputStream;
import java.nio.charset.StandardCharsets;
import java.util.ArrayList;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import org.json.JSONArray;
import org.json.JSONObject;

/** Closes a request's launch gate and reaps only its initial-environment token. */
final class GuestTools {
    private static final String ROOT = "/data/local/tmp/rgvm-owned-tools";
    private static final class Owned {
        final int pid; final String started, name;
        Owned(int pid, String started, String name) { this.pid = pid; this.started = started; this.name = name; }
        JSONObject json() throws Exception { return new JSONObject().put("pid", pid).put("startedTicks", started).put("name", name); }
    }
    private static final class Scan {
        final List<Owned> owned = new ArrayList<Owned>();
        final JSONArray errors = new JSONArray();
    }
    private static byte[] read(File path, int limit) throws Exception {
        try (FileInputStream input = new FileInputStream(path); ByteArrayOutputStream output = new ByteArrayOutputStream()) {
            byte[] buffer = new byte[4096]; int count;
            while ((count = input.read(buffer)) >= 0) {
                if (output.size() + count > limit) throw new IllegalStateException("Process metadata exceeds limit");
                output.write(buffer, 0, count);
            }
            return output.toByteArray();
        }
    }
    private static Owned observe(int pid, String token) throws Exception {
        File directory = new File("/proc/" + pid);
        String before = new String(read(new File(directory, "stat"), 8192), StandardCharsets.UTF_8);
        int end = before.lastIndexOf(')');
        String[] fields = before.substring(end + 2).split(" +");
        if (fields[0].equals("Z") || fields[0].equals("X")) return null;
        String environment = new String(read(new File(directory, "environ"), 1024 * 1024), StandardCharsets.UTF_8);
        boolean matches = false;
        for (String entry : environment.split("\u0000", -1)) if (entry.equals("RGVM_TOOL_ID=" + token)) { matches = true; break; }
        if (!matches) return null;
        String after = new String(read(new File(directory, "stat"), 8192), StandardCharsets.UTF_8);
        String[] current = after.substring(after.lastIndexOf(')') + 2).split(" +");
        if (!fields[19].equals(current[19]) || current[0].equals("Z") || current[0].equals("X")) return null;
        return new Owned(pid, fields[19], before.substring(before.indexOf('(') + 1, end));
    }
    private static Scan scan(String token) throws Exception {
        Scan result = new Scan(); File[] processes = new File("/proc").listFiles();
        if (processes == null) throw new IllegalStateException("Cannot enumerate guest processes");
        for (File directory : processes) {
            if (!directory.getName().matches("[0-9]+")) continue;
            int pid = Integer.parseInt(directory.getName());
            try { Owned value = observe(pid, token); if (value != null) result.owned.add(value); }
            catch (Exception error) {
                if (directory.exists()) result.errors.put(new JSONObject().put("pid", pid).put("error", error.getClass().getSimpleName()));
            }
        }
        return result;
    }
    private static void directory(File path) throws Exception {
        try { Os.mkdir(path.getPath(), 0711); }
        catch (ErrnoException error) { if (error.errno != OsConstants.EEXIST) throw error; }
        StructStat stat = Os.lstat(path.getPath());
        if (!OsConstants.S_ISDIR(stat.st_mode) || stat.st_uid != 0) throw new IllegalStateException("Tool control directory is not root-owned");
        Os.chmod(path.getPath(), 0711);
    }
    private static void marker(File path) throws Exception {
        FileDescriptor fd = Os.open(path.getPath(), OsConstants.O_WRONLY | OsConstants.O_CREAT | OsConstants.O_NOFOLLOW, 0444);
        try {
            StructStat stat = Os.fstat(fd);
            if (!OsConstants.S_ISREG(stat.st_mode) || stat.st_uid != 0) throw new IllegalStateException("Invalid tool control marker");
            Os.fchmod(fd, 0444); Os.fsync(fd);
        } finally { Os.close(fd); }
    }
    static void run(String encoded) {
        try {
            JSONObject request = new JSONObject(new String(Base64.decode(encoded, Base64.DEFAULT), StandardCharsets.UTF_8));
            String token = request.getString("token"), op = request.getString("op");
            if (!token.matches("[a-f0-9]{32}")) throw new IllegalArgumentException("Invalid tool token");
            directory(new File(ROOT)); File home = new File(ROOT, token); directory(home);
            File open = new File(home, "open"), closed = new File(home, "closed");
            JSONObject result;
            if (op.equals("prepare")) {
                if (closed.exists()) throw new IllegalStateException("Tool lease is closed");
                marker(open);
                if (closed.exists()) throw new IllegalStateException("Tool lease closed while preparing");
                result = new JSONObject().put("prepared", true).put("token", token);
            } else if (op.equals("cleanup")) {
                // The tombstone is retained: a delayed prepare or launch cannot reopen it.
                marker(closed);
                if (open.exists() && !open.delete()) throw new IllegalStateException("Cannot close tool launch gate");
                long deadline = android.os.SystemClock.elapsedRealtime() + 3000;
                Map<Integer, Owned> observed = new LinkedHashMap<Integer, Owned>();
                JSONArray signals = new JSONArray(); Scan current; int round = 0;
                do {
                    current = scan(token);
                    for (Owned process : current.owned) {
                        observed.put(process.pid, process);
                        try {
                            Owned fresh = observe(process.pid, token);
                            if (fresh == null || !fresh.started.equals(process.started)) continue;
                            int signal = round < 2 ? OsConstants.SIGTERM : OsConstants.SIGKILL;
                            Os.kill(fresh.pid, signal);
                            signals.put(fresh.json().put("signal", signal));
                        } catch (ErrnoException gone) { if (gone.errno != OsConstants.ESRCH) throw gone; }
                        catch (java.io.FileNotFoundException gone) { if (new File("/proc/" + process.pid).exists()) throw gone; }
                    }
                    Thread.sleep(100); round++;
                    if (current.owned.isEmpty() && round >= 2) break;
                } while (android.os.SystemClock.elapsedRealtime() < deadline);
                current = scan(token);
                JSONArray seen = new JSONArray(), remaining = new JSONArray();
                for (Owned process : observed.values()) seen.put(process.json());
                for (Owned process : current.owned) remaining.put(process.json());
                result = new JSONObject().put("token", token).put("gateClosed", true)
                    .put("clean", remaining.length() == 0 && current.errors.length() == 0)
                    .put("observed", seen).put("signals", signals).put("remaining", remaining).put("scanErrors", current.errors)
                    .put("guestElapsedMs", android.os.SystemClock.elapsedRealtime());
            } else throw new IllegalArgumentException("Unknown tool control operation");
            System.out.println(new JSONObject().put("ok", true).put("result", result));
        } catch (Throwable error) {
            try { System.out.println(new JSONObject().put("ok", false).put("error", new JSONObject().put("code", "guest_tool_control_failed").put("message", error.toString()))); }
            catch (Exception ignored) { System.err.println("RGVM guest tool control failed"); }
        }
        System.exit(0);
    }
}
