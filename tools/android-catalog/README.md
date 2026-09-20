# Android metadata bridge

This project is product-owned device I/O code, not an application plugin. It reads Android package, user, volume and file metadata in a short-lived `app_process` process. Requested file hashes stream file contents without returning them. Explicit transfer execution supports scoped staging, chunk verification, backup and commit. It does not install an APK, change the system image or start listed applications.

Labels, icons and data directories come from Android's [ApplicationInfo / PackageItemInfo APIs](https://developer.android.com/reference/android/content/pm/ApplicationInfo). System context initialization uses the framework's [ActivityThread](https://android.googlesource.com/platform/frameworks/base/+/refs/heads/android15-release/core/java/android/app/ActivityThread.java) through reflection, so compatibility requires a real-device check. Failures retain tool diagnostics rather than inventing app metadata. PID association uses ActivityManager's package membership lists, including the UID to distinguish users; a shared UID alone is not application identity.

The host verifies the embedded DEX digest, stages it in a root-owned directory on the verified VM, verifies the transfer, and uses it read-only. Results become structured application/file references and local 48px icon artifacts. The legacy `apps` command keeps its original package-array contract.

File metadata uses Android [Os](https://developer.android.com/reference/android/system/Os) and [StructStat](https://developer.android.com/reference/android/system/StructStat). Directory pages fingerprint all entries and reject changed cursors rather than silently dropping entries. Links are listed but never followed for browsing or hashing; opened file descriptors are checked against the resolved root. The maintenance volume inventory is filtered by user visibility and mounted state. The app-attributed `getVolumeList` route rejects UID 0 (`callingPackage does not match UID`), so it is not used. These reflection paths require real-device validation for a different Android image.

Transfer planning uses a bounded `walk` NDJSON stream with a mandatory completion record, preserving empty directories and per-file hashes. It also supports batched target observations and free-space queries. Existing file descriptors are explicitly closed after hashing, including on Android where a stream constructed from a supplied descriptor may not own it. These operations are read-only; a saved plan is not an executed transfer.

Binary download chunks use `adb exec-out`; `adb shell` may translate LF to CRLF and must not carry raw file bytes. The read mode checks the opened file's root and version, then emits at most8MiB per invocation. Uploads verify incoming chunk hashes and offsets in scoped staging files, then verify the whole source digest and destination precondition before backup/rename. User/application/session checks and the execution journal are owned by the host service. Retained partial files are recovery artifacts, not successful destinations.

Build inputs: JDK 21, SDK platform 36 revision 2, build-tools 35.0.0. The embedded manifest records source-tree, DEX, Android API JAR and D8 JAR hashes. The source-tree hash covers sorted `relative/java/path=sha256\n` rows, with each source normalized to UTF-8/LF. Source or compiler changes require regenerating the artifact and reviewing the manifest diff:

```powershell
./build/Build-AndroidCatalog.ps1 -SdkRoot C:/Android/Sdk -JavaHome C:/Java/jdk-21
./build/Build-AndroidCatalog.ps1 -SdkRoot C:/Android/Sdk -JavaHome C:/Java/jdk-21 -Verify
```

CI and release builds reconstruct the DEX and compare it with the embedded artifact. The required compile dependencies exist only on build machines; users do not need them. The platform API used to compile this helper does not replace or upgrade the VM's Android image. Runtime compilation is not performed.
