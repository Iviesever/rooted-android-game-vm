package dev.rgvm.catalog;

import android.system.Os;
import android.system.OsConstants;
import android.system.ErrnoException;
import android.system.StructStat;
import android.util.Base64;
import java.io.File;
import java.io.FileDescriptor;
import java.io.FileInputStream;
import java.io.FileOutputStream;
import java.nio.charset.StandardCharsets;
import java.security.MessageDigest;
import org.json.JSONObject;

final class FileTransfer {
    private static final long CHUNK = 8L * 1024 * 1024;
    private static FileDescriptor open(String root, String relative, String expected) throws Exception {
        File file = DeviceFiles.contained(root, relative, false);
        FileDescriptor descriptor = Os.open(file.getPath(), OsConstants.O_RDONLY | OsConstants.O_NOFOLLOW | OsConstants.O_NONBLOCK | OsConstants.O_CLOEXEC, 0);
        try {
            StructStat stat = Os.fstat(descriptor);
            if (!OsConstants.S_ISREG(stat.st_mode) || !DeviceFiles.version(stat).equals(expected)) throw new DeviceFiles.Failure("source_changed", "Source no longer matches the verified plan");
            int fd = (Integer) FileDescriptor.class.getMethod("getInt$").invoke(descriptor);
            if (!new File("/proc/self/fd/" + fd).getCanonicalPath().startsWith(new File(root).getCanonicalPath() + "/"))
                throw new DeviceFiles.Failure("path_escape", "Opened file escaped selected root");
            return descriptor;
        } catch (Throwable error) { close(descriptor); throw error; }
    }
    private static void close(FileDescriptor fd) throws Exception {
        try { Os.close(fd); } catch (ErrnoException error) { if (error.errno != OsConstants.EBADF) throw error; }
    }
    static void readChunk(String encoded) {
        try {
            JSONObject request = new JSONObject(new String(Base64.decode(encoded, Base64.DEFAULT), StandardCharsets.UTF_8));
            long offset = request.getLong("offset"), length = request.getLong("length");
            if (offset < 0 || length < 0 || length > CHUNK) throw new DeviceFiles.Failure("invalid_argument", "Invalid chunk range");
            FileDescriptor fd = open(request.getString("root"), request.getString("relativePath"), request.getString("version"));
            try {
                StructStat before = Os.fstat(fd);
                if (offset > before.st_size || length > before.st_size - offset) throw new DeviceFiles.Failure("source_changed", "Chunk exceeds current source");
                Os.lseek(fd, offset, OsConstants.SEEK_SET);
                FileInputStream input = new FileInputStream(fd); byte[] buffer = new byte[65536]; long left = length;
                while (left > 0) { int count = input.read(buffer, 0, (int) Math.min(left, buffer.length)); if (count < 0) throw new DeviceFiles.Failure("source_changed", "Source shortened"); System.out.write(buffer, 0, count); left -= count; }
                System.out.flush();
                if (!DeviceFiles.version(before).equals(DeviceFiles.version(Os.fstat(fd)))) throw new DeviceFiles.Failure("source_changed", "Source changed during reading");
            } finally { close(fd); }
            System.exit(0);
        } catch (Throwable error) { System.err.println("RGVM_FILE_ERROR:" + error.toString()); System.exit(1); }
    }
    private static String digest(File file, long length) throws Exception {
        MessageDigest digest = MessageDigest.getInstance("SHA-256");
        try (FileInputStream input = new FileInputStream(file)) {
            byte[] buffer = new byte[65536]; long left = length;
            while (left > 0) { int count = input.read(buffer, 0, (int) Math.min(left, buffer.length)); if (count < 0) throw new DeviceFiles.Failure("source_changed", "Staging file shortened"); digest.update(buffer, 0, count); left -= count; }
        }
        return DeviceFiles.hex(digest.digest());
    }
    private static File child(JSONObject request, String prefix) throws Exception {
        String id = request.getString("planId"); int index = request.getInt("index");
        if (!id.matches("[a-f0-9]{32}") || index < 0 || index >= 100000) throw new DeviceFiles.Failure("invalid_argument", "Invalid transfer identity");
        String relative = request.getString("relativePath");
        int slash = relative.lastIndexOf('/');
        File parent = DeviceFiles.contained(request.getString("root"), slash < 0 ? "" : relative.substring(0, slash), false);
        File file = new File(parent, ".rgvm-" + prefix + "-" + id + "-" + index);
        if (java.nio.file.Files.isSymbolicLink(file.toPath())) throw new DeviceFiles.Failure("path_escape", "Transfer staging path is a link");
        return file;
    }
    private static File target(JSONObject request) throws Exception {
        String relative = request.getString("relativePath");
        if (relative.length() == 0) throw new DeviceFiles.Failure("invalid_argument", "Cannot replace scope root");
        String[] parts = relative.split("/", -1);
        for (String part : parts) if (part.length() == 0 || part.equals(".") || part.equals("..")) throw new DeviceFiles.Failure("path_escape", "Invalid target path");
        int slash = relative.lastIndexOf('/');
        File parent = DeviceFiles.contained(request.getString("root"), slash < 0 ? "" : relative.substring(0, slash), false);
        File result = new File(parent, parts[parts.length - 1]);
        if (java.nio.file.Files.isSymbolicLink(result.toPath())) throw new DeviceFiles.Failure("path_escape", "Target is a link");
        return result;
    }
    private static void expected(File target, JSONObject expected) throws Exception { expected(target, expected, false); }
    private static void expected(File target, JSONObject expected, boolean moved) throws Exception {
        if (expected == null) { if (target.exists()) throw new DeviceFiles.Failure("target_changed", "New destination already exists"); return; }
        if (!target.exists()) throw new DeviceFiles.Failure("target_changed", "Destination disappeared");
        StructStat stat = Os.lstat(target.getPath());
        if (!moved && !DeviceFiles.version(stat).equals(expected.getString("version"))) throw new DeviceFiles.Failure("target_changed", "Destination changed since planning");
        if (moved && !(expected.getString("kind").equals("directory") ? target.isDirectory() : target.isFile())) throw new DeviceFiles.Failure("target_changed", "Backup type differs");
        if (expected.has("sha256") && !expected.isNull("sha256") && !digest(target, stat.st_size).equals(expected.getString("sha256")))
            throw new DeviceFiles.Failure("target_changed", "Destination content changed");
    }
    private static void permissions(File file, JSONObject request, boolean directory) throws Exception {
        int mode = Integer.parseInt(request.getString(directory ? "directoryMode" : "fileMode"), 8);
        if (request.optBoolean("privateData", false)) {
            Os.chown(file.getPath(), request.getInt("uid"), request.getInt("gid"));
        } else if (request.optBoolean("externalData", false)) {
            StructStat parent = Os.lstat(file.getParent());
            if (!OsConstants.S_ISDIR(parent.st_mode)) throw new DeviceFiles.Failure("path_escape", "Application storage parent changed");
            Os.chown(file.getPath(), request.getInt("applicationUid"), parent.st_gid);
            if (directory) mode |= parent.st_mode & OsConstants.S_ISGID;
        }
        // chown can clear setgid. Apply the final mode afterwards so descendants keep
        // the real volume's group rather than inheriting the root helper's group.
        Os.chmod(file.getPath(), mode);
        if (request.optBoolean("privateData", false)) {
            Process process = new ProcessBuilder("/system/bin/restorecon", file.getPath()).redirectErrorStream(true).start();
            while (process.getInputStream().read() >= 0) { }
            if (process.waitFor() != 0) throw new DeviceFiles.Failure("permission_restore_failed", "restorecon failed before commit");
        }
    }
    static Object execute(JSONObject request) throws Exception {
        String op = request.getString("op");
        if (op.equals("transfer-hash-prefix")) {
            FileDescriptor fd = open(request.getString("root"), request.getString("relativePath"), request.getString("version"));
            try {
                long count = request.getLong("length"); StructStat stat = Os.fstat(fd);
                if (count < 0 || count > stat.st_size) throw new DeviceFiles.Failure("source_changed", "Invalid prefix length");
                int value = (Integer) FileDescriptor.class.getMethod("getInt$").invoke(fd);
                String hash = digest(new File("/proc/self/fd/" + value), count);
                if (!DeviceFiles.version(stat).equals(DeviceFiles.version(Os.fstat(fd)))) throw new DeviceFiles.Failure("source_changed", "Source changed while validating prefix");
                return new JSONObject().put("sha256", hash);
            } finally { close(fd); }
        }
        File stage = child(request, "stage"), backup = child(request, "backup"), target = target(request);
        if (op.equals("transfer-state")) {
            JSONObject state = new JSONObject().put("stagePath", stage.getPath()).put("backupPath", backup.getPath()).put("backupExists", backup.exists()).put("targetExists", target.exists());
            if (stage.exists()) state.put("bytes", stage.length()).put("sha256", digest(stage, stage.length())); else state.put("bytes", 0);
            if (target.exists()) state.put("target", DeviceFiles.entry(target, request.getString("relativePath"), target.isFile(), new File(request.getString("root")).getCanonicalPath()));
            return state;
        }
        if (op.equals("transfer-append")) {
            String wire = request.getString("wire");
            String wireRoot = "/data/local/tmp/rgvm-transfer-wire/" + request.getString("planId") + "/";
            if (!new File(wire).getCanonicalPath().startsWith(wireRoot)) throw new DeviceFiles.Failure("path_escape", "Invalid wire staging path");
            File chunk = new File(wire); long count = request.getLong("length"), offset = request.getLong("offset");
            if (count < 0 || count > CHUNK || chunk.length() != count || !digest(chunk, count).equals(request.getString("sha256"))) throw new DeviceFiles.Failure("checksum_mismatch", "Chunk verification failed");
            if (request.optBoolean("reset", false)) { try (FileOutputStream reset = new FileOutputStream(stage)) { reset.getFD().sync(); } }
            if ((stage.exists() ? stage.length() : 0) != offset) throw new DeviceFiles.Failure("staging_changed", "Staging offset changed");
            try (FileInputStream input = new FileInputStream(chunk); FileOutputStream output = new FileOutputStream(stage, true)) {
                byte[] buffer = new byte[65536]; int read; while ((read = input.read(buffer)) >= 0) if (read > 0) output.write(buffer, 0, read);
                output.getFD().sync();
            }
            Os.chmod(stage.getPath(), 0600);
            return new JSONObject().put("bytes", stage.length()).put("stagePath", stage.getPath());
        }
        if (!op.equals("transfer-commit") && !op.equals("transfer-mkdir")) throw new DeviceFiles.Failure("invalid_argument", "Unknown transfer operation");
        JSONObject prior = request.optJSONObject("expectedTarget");
        if (op.equals("transfer-mkdir") && target.isDirectory() && prior != null && prior.getString("kind").equals("directory")) return new JSONObject().put("created", false);
        if (backup.exists()) {
            if (!request.optBoolean("recover", false) || target.exists()) throw new DeviceFiles.Failure("recovery_required", "Existing backup requires explicit recovery");
            expected(backup, prior, true);
        } else expected(target, prior);
        if (op.equals("transfer-commit")) {
            if (!stage.isFile() || stage.length() != request.getLong("length") || !digest(stage, stage.length()).equals(request.getString("sha256")))
                throw new DeviceFiles.Failure("checksum_mismatch", "Final staging content mismatch");
            permissions(stage, request, false);
            if (request.has("modifiedUnixMs") && !request.isNull("modifiedUnixMs")) stage.setLastModified(request.getLong("modifiedUnixMs"));
        }
        if (target.exists()) Os.rename(target.getPath(), backup.getPath());
        try {
            if (op.equals("transfer-mkdir")) { Os.mkdir(target.getPath(), 0700); permissions(target, request, true); }
            else Os.rename(stage.getPath(), target.getPath());
        } catch (Throwable error) { if (!target.exists() && backup.exists()) Os.rename(backup.getPath(), target.getPath()); throw error; }
        FileDescriptor parent = Os.open(target.getParent(), OsConstants.O_RDONLY | OsConstants.O_NOFOLLOW, 0);
        try { if (!OsConstants.S_ISDIR(Os.fstat(parent).st_mode)) throw new DeviceFiles.Failure("path_escape", "Target parent changed"); Os.fsync(parent); } finally { close(parent); }
        JSONObject result = new JSONObject().put("target", DeviceFiles.entry(target, request.getString("relativePath"), target.isFile(), new File(request.getString("root")).getCanonicalPath()));
        if (backup.exists()) result.put("backupPath", backup.getPath());
        return result;
    }
}
