package dev.rgvm.catalog;

import android.content.Context;
import android.os.UserHandle;
import android.os.UserManager;
import android.os.storage.StorageManager;
import android.system.ErrnoException;
import android.system.Os;
import android.system.OsConstants;
import android.system.StructStat;
import android.util.Base64;
import java.io.File;
import java.io.FileDescriptor;
import java.io.FileInputStream;
import java.nio.charset.StandardCharsets;
import java.nio.file.DirectoryStream;
import java.nio.file.Files;
import java.nio.file.Path;
import java.security.MessageDigest;
import java.util.ArrayList;
import java.util.Collections;
import java.util.List;
import org.json.JSONArray;
import org.json.JSONObject;

final class DeviceFiles {
    private static final int MAX_ENTRIES = 100000;
    private static final class Failure extends Exception {
        final String code;
        Failure(String code, String message) { super(message); this.code = code; }
    }
    static void run(String encoded) {
        try {
            if (encoded.length() > 131072) throw new Failure("invalid_argument", "File request exceeds size limit");
            JSONObject request = new JSONObject(new String(Base64.decode(encoded, Base64.DEFAULT), StandardCharsets.UTF_8));
            String op = request.getString("op");
            Object result;
            if (op.equals("users")) result = users();
            else if (op.equals("storage")) result = storage(request.getInt("userId"));
            else if (op.equals("probe")) {
                JSONArray rows = new JSONArray();
                JSONArray paths = request.getJSONArray("paths");
                if (paths.length() > 64) throw new Failure("invalid_argument", "Too many roots");
                for (int i = 0; i < paths.length(); i++) rows.put(probe(paths.getString(i)));
                result = rows;
            } else if (op.equals("list") || op.equals("stat")) result = inspect(request, op.equals("list"));
            else throw new Failure("invalid_argument", "Unsupported file operation");
            System.out.println(new JSONObject().put("ok", true).put("result", result).toString());
            System.exit(0);
        } catch (Throwable error) {
            while (error instanceof java.lang.reflect.InvocationTargetException && error.getCause() != null) error = error.getCause();
            if (!(error instanceof Failure)) error.printStackTrace(System.err);
            try {
                String code = error instanceof Failure ? ((Failure) error).code : "file_observation_failed";
                if (error instanceof ErrnoException) {
                    int errno = ((ErrnoException) error).errno;
                    code = errno == OsConstants.ENOENT ? "path_not_found" : errno == OsConstants.EACCES ? "permission_denied" : "file_io_error";
                }
                System.out.println(new JSONObject().put("ok", false).put("error",
                    new JSONObject().put("code", code).put("message", error.toString())).toString());
                System.exit(0);
            } catch (Throwable fatal) { error.printStackTrace(System.err); System.exit(1); }
        }
    }
    private static boolean unlocked(UserManager manager, int user) throws Exception {
        try {
            UserHandle handle = (UserHandle) UserHandle.class.getMethod("of", int.class).invoke(null, user);
            return (Boolean) UserManager.class.getMethod("isUserUnlocked", UserHandle.class).invoke(manager, handle);
        } catch (NoSuchMethodException absent) {
            return (Boolean) UserManager.class.getMethod("isUserUnlocked", int.class).invoke(manager, user);
        }
    }
    private static JSONArray users() throws Exception {
        UserManager manager = (UserManager) Main.context(0).getSystemService(Context.USER_SERVICE);
        List<?> users = (List<?>) UserManager.class.getMethod("getUsers").invoke(manager);
        JSONArray result = new JSONArray();
        for (Object user : users) {
            int id = user.getClass().getField("id").getInt(user);
            result.put(new JSONObject().put("userId", id).put("name", user.getClass().getField("name").get(user))
                .put("unlocked", unlocked(manager, id)));
        }
        return result;
    }
    private static JSONObject storage(int user) throws Exception {
        if (user < 0) throw new Failure("invalid_argument", "Invalid user");
        Context context = Main.context(user);
        UserManager manager = (UserManager) context.getSystemService(Context.USER_SERVICE);
        Object info = UserManager.class.getMethod("getUserInfo", int.class).invoke(manager, user);
        if (info == null) throw new Failure("user_not_found", "Android user does not exist");
        StorageManager storage = (StorageManager) context.getSystemService(Context.STORAGE_SERVICE);
        // Use the maintenance volume inventory. The app-attributed getVolumeList API
        // cannot associate UID 0 with an application package.
        List<?> inventory = (List<?>) StorageManager.class.getMethod("getVolumes").invoke(storage);
        JSONArray volumes = new JSONArray();
        for (Object volume : inventory) {
            Class<?> type = volume.getClass();
            if (!(Boolean) type.getMethod("isVisibleForUser", int.class).invoke(volume, user) ||
                !(Boolean) type.getMethod("isMountedReadable").invoke(volume)) continue;
            File path = (File) type.getMethod("getPathForUser", int.class).invoke(volume, user);
            if (path != null) volumes.put(new JSONObject().put("path", path.getAbsolutePath()).put("primary", type.getMethod("isPrimary").invoke(volume)));
        }
        return new JSONObject().put("userId", user).put("name", info.getClass().getField("name").get(info))
            .put("unlocked", unlocked(manager, user)).put("volumes", volumes);
    }
    private static JSONObject probe(String path) throws Exception {
        JSONObject result = new JSONObject().put("path", path);
        try {
            StructStat stat = Os.lstat(path);
            if (OsConstants.S_ISLNK(stat.st_mode)) return result.put("exists", true).put("accessible", false).put("reason", "root_is_link");
            if (!OsConstants.S_ISDIR(stat.st_mode)) return result.put("exists", true).put("accessible", false).put("reason", "not_directory");
            return result.put("exists", true).put("accessible", Os.access(path, OsConstants.R_OK | OsConstants.X_OK))
                .put("writable", Os.access(path, OsConstants.W_OK | OsConstants.X_OK)).put("version", version(stat));
        } catch (ErrnoException error) {
            if (error.errno == OsConstants.ENOENT) return result.put("exists", false).put("accessible", false).put("reason", "not_created");
            return result.put("exists", true).put("accessible", false).put("reason", "permission_denied");
        }
    }
    private static File contained(String root, String relative, boolean allowFinalLink) throws Exception {
        if (!root.startsWith("/") || relative.startsWith("/") || relative.indexOf('\0') >= 0)
            throw new Failure("path_escape", "Root-relative path required");
        File base = new File(root);
        if (OsConstants.S_ISLNK(Os.lstat(base.getAbsolutePath()).st_mode)) throw new Failure("path_escape", "Root itself is a symbolic link");
        File canonicalRoot = base.getCanonicalFile();
        File current = canonicalRoot;
        if (relative.length() > 0) {
            String[] parts = relative.split("/", -1);
            for (int i = 0; i < parts.length; i++) {
                if (parts[i].length() == 0 || parts[i].equals(".") || parts[i].equals("..")) throw new Failure("path_escape", "Invalid path component");
                current = new File(current, parts[i]);
                boolean link = OsConstants.S_ISLNK(Os.lstat(current.getAbsolutePath()).st_mode);
                if (link && (i < parts.length - 1 || !allowFinalLink)) throw new Failure("path_escape", "Symbolic links are not followed");
            }
        }
        File parent = relative.length() == 0 ? canonicalRoot : current.getParentFile().getCanonicalFile();
        if (!parent.equals(canonicalRoot) && !parent.getPath().startsWith(canonicalRoot.getPath() + "/")) throw new Failure("path_escape", "Path escaped selected root");
        return current;
    }
    private static String hex(byte[] value) {
        char[] alphabet = "0123456789abcdef".toCharArray(), result = new char[value.length * 2];
        for (int i = 0; i < value.length; i++) { result[2 * i] = alphabet[(value[i] & 255) >>> 4]; result[2 * i + 1] = alphabet[value[i] & 15]; }
        return new String(result);
    }
    private static String version(StructStat stat) throws Exception {
        String identity = stat.st_dev + ":" + stat.st_ino + ":" + stat.st_mode + ":" + stat.st_uid + ":" + stat.st_gid + ":" + stat.st_size + ":" +
            stat.st_mtim.tv_sec + ":" + stat.st_mtim.tv_nsec + ":" + stat.st_ctim.tv_sec + ":" + stat.st_ctim.tv_nsec;
        return hex(MessageDigest.getInstance("SHA-256").digest(identity.getBytes(StandardCharsets.UTF_8)));
    }
    private static JSONObject entry(File file, String relative, boolean hash, String allowedRoot) throws Exception {
        StructStat stat = Os.lstat(file.getPath());
        String kind = OsConstants.S_ISDIR(stat.st_mode) ? "directory" : OsConstants.S_ISREG(stat.st_mode) ? "file" : OsConstants.S_ISLNK(stat.st_mode) ? "symlink" : "special";
        String original = version(stat);
        JSONObject row = new JSONObject().put("name", file.getName()).put("relativePath", relative).put("kind", kind)
            .put("bytes", stat.st_size).put("modifiedUnixMs", stat.st_mtim.tv_sec * 1000 + stat.st_mtim.tv_nsec / 1000000)
            .put("uid", stat.st_uid).put("gid", stat.st_gid).put("mode", Integer.toOctalString(stat.st_mode & 07777)).put("version", original);
        if (kind.equals("symlink")) row.put("linkTarget", Os.readlink(file.getPath()));
        if (hash) {
            if (!kind.equals("file")) throw new Failure("unsupported_entry", "Hashes require a regular file");
            FileDescriptor descriptor = Os.open(file.getPath(), OsConstants.O_RDONLY | OsConstants.O_NOFOLLOW | OsConstants.O_NONBLOCK | OsConstants.O_CLOEXEC, 0);
            try (FileInputStream input = new FileInputStream(descriptor)) {
                int fd = (Integer) FileDescriptor.class.getMethod("getInt$").invoke(descriptor);
                String opened = new File("/proc/self/fd/" + fd).getCanonicalPath();
                if (!opened.startsWith(allowedRoot + "/")) throw new Failure("path_escape", "Opened file escaped selected root");
                if (!original.equals(version(Os.fstat(descriptor)))) throw new Failure("source_changed", "File changed before reading");
                MessageDigest digest = MessageDigest.getInstance("SHA-256");
                byte[] buffer = new byte[65536]; int count;
                while ((count = input.read(buffer)) >= 0) if (count > 0) digest.update(buffer, 0, count);
                if (!original.equals(version(Os.fstat(descriptor))) || !original.equals(version(Os.lstat(file.getPath()))))
                    throw new Failure("source_changed", "File changed while hashing");
                row.put("sha256", hex(digest.digest()));
            }
        }
        return row;
    }
    private static Object inspect(JSONObject request, boolean list) throws Exception {
        String root = request.getString("root"), relative = request.optString("relativePath", "");
        File target = contained(root, relative, !list && !request.optBoolean("hash", false));
        JSONObject self = entry(target, relative, !list && request.optBoolean("hash", false), new File(root).getCanonicalPath());
        if (!list) return self;
        if (!self.getString("kind").equals("directory")) throw new Failure("not_directory", "Selected entry is not a directory");
        int offset = request.optInt("offset", 0), limit = request.optInt("limit", 200);
        if (offset < 0 || limit < 1 || limit > 500) throw new Failure("invalid_argument", "Invalid directory page");
        List<String> names = new ArrayList<String>();
        try (DirectoryStream<Path> stream = Files.newDirectoryStream(target.toPath())) {
            for (Path child : stream) {
                names.add(child.getFileName().toString());
                if (names.size() > MAX_ENTRIES) throw new Failure("directory_limit", "Directory exceeds 100000 entries; no truncated result was returned");
            }
        }
        Collections.sort(names);
        if (offset > names.size()) throw new Failure("stale_cursor", "Directory page is out of range");
        MessageDigest digest = MessageDigest.getInstance("SHA-256");
        digest.update(self.getString("version").getBytes(StandardCharsets.UTF_8));
        JSONArray rows = new JSONArray();
        for (int i = 0; i < names.size(); i++) {
            String name = names.get(i);
            String child = relative.length() == 0 ? name : relative + "/" + name;
            JSONObject metadata = entry(new File(target, name), child, false, null);
            digest.update(name.getBytes(StandardCharsets.UTF_8)); digest.update((byte) 0);
            digest.update(metadata.getString("version").getBytes(StandardCharsets.UTF_8));
            if (i >= offset && i < offset + limit) rows.put(metadata);
        }
        if (!self.getString("version").equals(version(Os.lstat(target.getPath())))) throw new Failure("source_changed", "Directory changed during enumeration");
        String snapshot = hex(digest.digest());
        if (request.has("snapshot") && !snapshot.equals(request.getString("snapshot"))) throw new Failure("stale_cursor", "Directory contents changed; restart enumeration");
        return new JSONObject().put("directory", self).put("entries", rows).put("snapshot", snapshot).put("total", names.size())
            .put("offset", offset).put("nextOffset", offset + rows.length() < names.size() ? offset + rows.length() : JSONObject.NULL);
    }
}
