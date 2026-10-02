# TbclSnapshotReader (TanukiBCL v3.2.8)

This helper is an exact source copy of the released upstream v3.2.8 `tools/TbclSnapshotReader/Program.cs`. It resolves and reads NoS `Nebula.Collab.TBCLFields` without writing to the Among Us process. NoS v3.5.3 removed `RequireUpdate` and continuously publishes snapshots.

```powershell
TbclSnapshotReader.exe layout <PID>
TbclSnapshotReader.exe palette <PID>
TbclSnapshotReader.exe resolve <PID> [tbcl-metadata.json]
TbclSnapshotReader.exe read <PID> tbcl-metadata.json
```

`layout` and `resolve` use ClrMD to find the `Latest` slot and nested structure offsets. The primitive `nextIndex` field supplies the non-GC static base needed for the pointer-typed `Latest` field. `palette` resolves `DynamicPalette.PlayerColors`. `read` consumes the latest published unmanaged snapshot using `PROCESS_VM_READ | PROCESS_QUERY_INFORMATION`. None of these commands requests write access or calls `WriteProcessMemory`.

Metadata is bound to the PID and process start time; resolve again after restarting the game. This .NET port intentionally builds and packages only the `win-x64` helper because 32-bit Among Us is outside its supported scope.
