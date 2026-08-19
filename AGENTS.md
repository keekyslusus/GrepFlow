# Repository Guidelines

- Keep `Main.cs` as a thin Flow Launcher adapter.
- Assemble the runtime dependency graph only in `GrepFlow/CompositionRoot.cs`.
- Prefer composition and constructor injection. Do not introduce implementation inheritance, abstract base classes, class hierarchies, or service locators.
- No XML docs (`/// <summary>`) and no comments that restate the code; comment only non-obvious why.
- When adding a workspace source for a file manager or code editor, keep the absent-application path cheap. Do not probe installation directories or perform session-file parsing, recursive filesystem enumeration, or process-tree scans unless the source has positively matched a foreground window or has cached active state. `GetActiveFolderAsync` must return quickly for unavailable or never-activated applications.
- Add tests verifying that a workspace source does not invoke its session reader or other expensive discovery when the application has never been detected as active.