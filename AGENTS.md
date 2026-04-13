## C# Coding Rules

- Naming convention for private fields: no `_` prefix
- Use `var` for local variables
- Don't use explicit `private` accessibility modifier
- Use English for comments

## Working Rules

- User is connecting the latest Xiaomi Android device via USB-C
- You can use the `adb` command for troubleshooting
- **After code changes, prefer `dotnet build -t:Run`** to build and launch on the connected Android device. This is the default verification step.

## .NET for Android (.NET 10) — custom views and JNI peers

- **Do not subclass Java widget types in C#** (e.g. `HorizontalScrollView`, `ViewGroup`) when the app uses **CoreCLR** (`UseMonoRuntime=false`, the default in this repo’s Android projects). The build may still emit Java stubs, but runtime activation can fail with `**no Java peer type found`** / `NotSupportedException`.
- Prefer **framework widgets + composition** (overlays, touch listeners, `RequestDisallowInterceptTouchEvent`, etc.), `**UseMonoRuntime=true`** if you truly need managed subclasses, or a **thin `AndroidJavaSource` `.java`** subclass wired with `[Register]`.
- CoreCLR on Android is still treated as **experimental** by the workload (see build warning **XA1040**).

### References

- [dotnet/android #10798 — TrimmableTypeMap / Java peer scanner (CoreCLR / NativeAOT context; .NET 11 milestone)](https://github.com/dotnet/android/issues/10798)
- [dotnet/android #10789 — TrimmableTypeMap umbrella](https://github.com/dotnet/android/issues/10789)
- [.NET for Android overview](https://learn.microsoft.com/en-us/dotnet/android/) (workload docs; runtime choice is project MSBuild, e.g. `UseMonoRuntime`)
- [.NET MAUI — Runtimes and compilation (Android Mono vs CoreCLR)](https://learn.microsoft.com/en-us/dotnet/maui/deployment/runtimes-compilation?view=net-maui-10.0) (same `UseMonoRuntime` idea on Android targets)