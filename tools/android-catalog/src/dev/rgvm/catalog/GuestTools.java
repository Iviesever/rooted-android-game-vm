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
    static final String ROOT = "/data/local/tmp/rgvm-owned-tools";
    static final class Owned {
        final int pid, group, session; final String started, name; int anchor; String anchorStarted;
        Owned(int pid, String started, String name, int group, int session) { this.pid = pid; this.started = started; this.name = name; this.group = group; this.session = session; }
        JSONObject json() throws Exception { return new JSONObject().put("pid", pid).put("startedTicks", started).put("name", name).put("processGroup", group).put("processSession", session); }
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
        return new Owned(pid, fields[19], before.substring(before.indexOf('(') + 1, end), Integer.parseInt(current[2]), Integer.parseInt(current[3]));
    }
    static Owned identity(int pid) throws Exception {
        String stat = new String(read(new File("/proc/" + pid + "/stat"), 8192), StandardCharsets.UTF_8);
        int end = stat.lastIndexOf(')'); String[] fields = stat.substring(end + 2).split(" +");
        if (fields[0].equals("Z") || fields[0].equals("X")) return null;
        return new Owned(pid, fields[19], stat.substring(stat.indexOf('(') + 1, end), Integer.parseInt(fields[2]), Integer.parseInt(fields[3]));
    }
    static List<Owned> members(int group, int session) throws Exception {
        List<Owned> result = new ArrayList<Owned>(); File[] directories = new File("/proc").listFiles();
        if (directories == null) throw new IllegalStateException("Cannot enumerate process group");
        for (File directory : directories) if (directory.getName().matches("[0-9]+")) {
            try { Owned value = identity(Integer.parseInt(directory.getName())); if (value != null && value.group == group && value.session == session) result.add(value); }
            catch (Exception gone) { if (directory.exists()) throw gone; }
        }
        return result;
    }
    private static Owned confirm(Owned process, String token) throws Exception {
        Owned direct = observe(process.pid, token);
        if (direct != null && direct.started.equals(process.started)) return direct;
        if (process.anchor == 0) return null;
        Owned anchor = observe(process.anchor, token), current = identity(process.pid);
        return anchor != null && anchor.started.equals(process.anchorStarted) && anchor.group == anchor.pid && anchor.session == anchor.pid &&
            current != null && current.started.equals(process.started) && current.group == anchor.pid && current.session == anchor.pid ? current : null;
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
        File groupDirectory = new File(ROOT + "/" + token + "/groups");
        if (groupDirectory.exists()) {
            if (!OsConstants.S_ISDIR(Os.lstat(groupDirectory.getPath()).st_mode)) throw new IllegalStateException("Invalid process group directory");
            File[] records = groupDirectory.listFiles();
            if (records == null || records.length > 1024) throw new IllegalStateException("Invalid process group inventory");
            for (File record : records) try {
                if (!record.getName().matches("[0-9]+-[0-9]+\\.json") || !OsConstants.S_ISREG(Os.lstat(record.getPath()).st_mode)) throw new IllegalStateException("Invalid process group record");
                JSONObject data = new JSONObject(new String(read(record, 4096), StandardCharsets.UTF_8));
                int pid = data.getInt("pid"); String started = data.getString("startedTicks");
                if (!record.getName().equals(pid + "-" + started + ".json")) throw new IllegalStateException("Process group identity mismatch");
                Owned leader = null; try { leader = observe(pid, token); } catch (Exception gone) { if (new File("/proc/" + pid).exists()) throw gone; }
                List<Owned> children = members(pid, pid);
                if (leader == null || !leader.started.equals(started) || leader.group != pid || leader.session != pid) {
                    if (!children.isEmpty()) result.errors.put(new JSONObject().put("code", "group_owner_unverified").put("group", pid));
                    continue;
                }
                for (Owned child : children) {
                    boolean present = false;
                    for (Owned existing : result.owned) if (existing.pid == child.pid) { present = true; break; }
                    if (!present) { child.anchor = pid; child.anchorStarted = started; result.owned.add(child); }
                }
            } catch (Exception error) {
                if (record.exists()) {
                    boolean gone = false;
                    if (record.getName().matches("[0-9]+-[0-9]+\\.json")) {
                        int pid = Integer.parseInt(record.getName().split("-")[0]); gone = members(pid, pid).isEmpty();
                    }
                    if (!gone) result.errors.put(new JSONObject().put("code", "group_record_unverified").put("error", error.getClass().getSimpleName()));
                }
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
                File groups = new File(home, "groups"); directory(groups); Os.chown(groups.getPath(), 0, 2000); Os.chmod(groups.getPath(), 0730);
                marker(open);
                if (closed.exists()) throw new IllegalStateException("Tool lease closed while preparing");
                result = new JSONObject().put("prepared", true).put("token", token);
            } else if (op.equals("cleanup")) {
                // The tombstone is retained: a delayed prepare or launch cannot reopen it.
                marker(closed);
                if (open.exists() && !open.delete()) throw new IllegalStateException("Cannot close tool launch gate");
                long started = android.os.SystemClock.elapsedRealtime();
                long grace = Math.max(200, Math.min(3000, request.optInt("graceMilliseconds", 200)));
                long deadline = started + grace + 3000;
                Map<Integer, Owned> observed = new LinkedHashMap<Integer, Owned>();
                JSONArray signals = new JSONArray(); Scan current; int round = 0;
                do {
                    current = scan(token);
                    for (Owned process : current.owned) {
                        observed.put(process.pid, process);
                        try {
                            // Keep an authenticated group leader alive until its descendants have exited.
                            boolean hasChildren = false;
                            if (process.pid == process.group) for (Owned child : current.owned)
                                if (child.pid != process.pid && child.group == process.pid && child.session == process.pid) { hasChildren = true; break; }
                            if (hasChildren) continue;
                            Owned fresh = confirm(process, token);
                            if (fresh == null || !fresh.started.equals(process.started)) continue;
                            int signal = android.os.SystemClock.elapsedRealtime() - started < grace ? OsConstants.SIGTERM : OsConstants.SIGKILL;
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
