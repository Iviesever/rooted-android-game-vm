# Android application catalog

This project is product-owned metadata code, not an application plugin. It reads Android package metadata in a short-lived `app_process` process. It does not install an APK, change the system image, start the listed applications, or read their private file contents.

Labels, icons and data directories come from Android's [ApplicationInfo / PackageItemInfo APIs](https://developer.android.com/reference/android/content/pm/ApplicationInfo). System context initialization uses the framework's [ActivityThread](https://android.googlesource.com/platform/frameworks/base/+/refs/heads/android15-release/core/java/android/app/ActivityThread.java) through reflection, so compatibility requires a real-device check. Failures retain tool diagnostics rather than inventing app metadata. PID association uses ActivityManager's package membership lists, including the UID to distinguish users; a shared UID alone is not application identity.

The host verifies the embedded DEX digest, stages it in a root-owned directory on the verified VM, verifies the transfer, and uses it read-only. Results become structured application references and local 48px icon artifacts. The legacy `apps` command keeps its original package-array contract.

Build inputs: JDK 21, SDK platform 36 revision 2, build-tools 35.0.0. The embedded manifest records source (UTF-8 with LF), DEX, Android API JAR and D8 JAR hashes. Source or compiler changes require regenerating the artifact and reviewing the manifest diff:

```powershell
./build/Build-AndroidCatalog.ps1 -SdkRoot C:/Android/Sdk -JavaHome C:/Java/jdk-21
./build/Build-AndroidCatalog.ps1 -SdkRoot C:/Android/Sdk -JavaHome C:/Java/jdk-21 -Verify
```

CI and release builds reconstruct the DEX and compare it with the embedded artifact. The required compile dependencies exist only on build machines; users do not need them. The platform API used to compile this helper does not replace or upgrade the VM's Android image. Runtime compilation is not performed.
