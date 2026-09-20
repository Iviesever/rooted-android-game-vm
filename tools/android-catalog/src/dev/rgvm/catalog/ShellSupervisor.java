package dev.rgvm.catalog;

import android.system.Os;
import android.system.OsConstants;
import android.util.Base64;
import java.io.File;
import java.io.FileOutputStream;
import java.io.InputStream;
import java.io.OutputStream;
import java.nio.charset.StandardCharsets;
import java.util.List;
import java.util.concurrent.atomic.AtomicReference;
import org.json.JSONObject;

/** Foreground shell lifetime, including ordinary children that clear their environment. */
final class ShellSupervisor {
    private static Thread copy(InputStream input, OutputStream output, AtomicReference<Throwable> failure) {
        Thread thread = new Thread(new Runnable() { public void run() {
            try { byte[] buffer = new byte[16384]; int count; while ((count = input.read(buffer)) >= 0) if (count > 0) { output.write(buffer, 0, count); output.flush(); } }
            catch (Throwable error) { failure.compareAndSet(null, error); }
        }});
        thread.start(); return thread;
    }
    static void run(String encoded) {
        int exit = 125; File record = null; int pid = android.os.Process.myPid();
        try {
            JSONObject args = new JSONObject(new String(Base64.decode(encoded, Base64.DEFAULT), StandardCharsets.UTF_8));
            String token = args.getString("token");
            if (!token.matches("[a-f0-9]{32}") || !token.equals(System.getenv("RGVM_TOOL_ID")) || (Os.getuid() != 0 && Os.getuid() != 2000))
                throw new IllegalArgumentException("Invalid foreground shell identity");
            File home = new File(GuestTools.ROOT, token);
            if (!new File(home, "open").isFile() || new File(home, "closed").exists()) throw new IllegalStateException("Shell launch gate closed");
            GuestTools.Owned self = GuestTools.identity(pid);
            if (self == null || self.group != pid || self.session != pid) throw new IllegalStateException("Shell requires its own process session");
            record = new File(new File(home, "groups"), pid + "-" + self.started + ".json");
            if (!record.createNewFile()) throw new IllegalStateException("Shell group record already exists");
            Os.chmod(record.getPath(), 0600);
            try (FileOutputStream output = new FileOutputStream(record)) { output.write(self.json().toString().getBytes(StandardCharsets.UTF_8)); output.getFD().sync(); }
            if (!new File(home, "open").isFile() || new File(home, "closed").exists()) throw new IllegalStateException("Shell launch gate closed");
            Process child = new ProcessBuilder("/system/bin/sh", "-c", args.getString("script")).start();
            child.getOutputStream().close();
            AtomicReference<Throwable> failure = new AtomicReference<Throwable>();
            Thread stdout = copy(child.getInputStream(), System.out, failure), stderr = copy(child.getErrorStream(), System.err, failure);
            exit = child.waitFor(); stdout.join(); stderr.join();
            if (failure.get() != null) throw new IllegalStateException("Shell output pipe failed", failure.get());
            long deadline = android.os.SystemClock.elapsedRealtime() + 2500; int round = 0;
            while (true) {
                List<GuestTools.Owned> children = GuestTools.members(pid, pid);
                boolean remaining = false;
                for (GuestTools.Owned member : children) if (member.pid != pid) {
                    remaining = true; GuestTools.Owned current = GuestTools.identity(member.pid);
                    if (current != null && current.started.equals(member.started) && current.group == pid && current.session == pid)
                        Os.kill(member.pid, round < 2 ? OsConstants.SIGTERM : OsConstants.SIGKILL);
                }
                if (!remaining) break;
                if (android.os.SystemClock.elapsedRealtime() > deadline) throw new IllegalStateException("Shell descendants require explicit cleanup");
                Thread.sleep(100); round++;
            }
            if (!record.delete()) throw new IllegalStateException("Cannot retire shell group record");
        } catch (Throwable error) { System.err.println("RGVM_SHELL_SUPERVISOR:" + error); exit = 125; }
        System.out.flush(); System.err.flush(); System.exit(exit);
    }
}
