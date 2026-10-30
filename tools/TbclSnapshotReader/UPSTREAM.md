# TbclSnapshotReader (TanukiBCL v3.2.21)

This helper follows the released upstream v3.2.21 `tools/TbclSnapshotReader` reader. `NosBody.cs` and `NosRoles.cs` are source copies from that release; `Program.cs` retains the .NET port's earlier schema and safety checks. It reads NoS `Nebula.Collab.TBCLFields` and managed roles without writing to the Among Us process. NoS v3.5.3.6 publishes schema `20261009` body fields.

The role reader includes later non-suspending attachment and translation-read optimizations. It reads labels only for active roles and stops once they are resolved, retaining Guest > Current > Default language precedence and validating dictionary changes on each read.

NoS metadata and palette resolution also use non-suspending attachment. Resolution validates initialized static fields and stable static-slot addresses; palette resolution additionally checks the live array reference, method table and 32-color length before and after deriving its layout. A changed or unavailable publication is rejected instead of returning a mixed layout. This does not turn live ClrMD inspection into a coherent snapshot, and real-game timing remains unmeasured.

```powershell
TbclSnapshotReader.exe layout <PID>
TbclSnapshotReader.exe palette <PID>
TbclSnapshotReader.exe roles <PID>
TbclSnapshotReader.exe resolve <PID> [tbcl-metadata.json]
TbclSnapshotReader.exe read <PID> tbcl-metadata.json
```

`layout` and `resolve` use ClrMD to find the `Latest` slot and nested structure offsets. The primitive `nextIndex` field supplies the non-GC static base needed for the pointer-typed `Latest` field. `palette` resolves `DynamicPalette.PlayerColors`; `roles` reads managed role assignments and optional body layout. `read` consumes the latest published unmanaged snapshot using `PROCESS_VM_READ | PROCESS_QUERY_INFORMATION`. None of these commands requests write access or calls `WriteProcessMemory`.

Metadata is bound to the PID and process start time; resolve again after restarting the game. This .NET port intentionally builds and packages only the `win-x64` helper because 32-bit Among Us is outside its supported scope.
